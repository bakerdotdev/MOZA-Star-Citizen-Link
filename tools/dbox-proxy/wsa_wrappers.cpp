#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <stdio.h>

#include "wsa_wrappers.h"
#include "iat_hook.h"
#include "logger.h"

typedef int (WSAAPI *fn_WSASendTo)(
    SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD,
    const sockaddr*, int,
    LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);

typedef int (WSAAPI *fn_WSASend)(
    SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD,
    LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);

typedef int (WSAAPI *fn_WSARecvFrom)(
    SOCKET, LPWSABUF, DWORD, LPDWORD, LPDWORD,
    sockaddr*, LPINT,
    LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);

typedef int (WSAAPI *fn_WSARecv)(
    SOCKET, LPWSABUF, DWORD, LPDWORD, LPDWORD,
    LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);

typedef SOCKET (WSAAPI *fn_WSASocketW)(
    int, int, int, LPWSAPROTOCOL_INFOW, GROUP, DWORD);

typedef int (WSAAPI *fn_sendto)(SOCKET, const char*, int, int, const sockaddr*, int);
typedef int (WSAAPI *fn_send)(SOCKET, const char*, int, int);
typedef int (WSAAPI *fn_recvfrom)(SOCKET, char*, int, int, sockaddr*, int*);
typedef int (WSAAPI *fn_recv)(SOCKET, char*, int, int);
typedef int (WSAAPI *fn_bind)(SOCKET, const sockaddr*, int);
typedef int (WSAAPI *fn_connect)(SOCKET, const sockaddr*, int);

namespace {

fn_WSASendTo   real_WSASendTo   = NULL;
fn_WSASend     real_WSASend     = NULL;
fn_WSARecvFrom real_WSARecvFrom = NULL;
fn_WSARecv     real_WSARecv     = NULL;
fn_WSASocketW  real_WSASocketW  = NULL;
fn_sendto      real_sendto      = NULL;
fn_send        real_send        = NULL;
fn_recvfrom    real_recvfrom    = NULL;
fn_recv        real_recv        = NULL;
fn_bind        real_bind        = NULL;
fn_connect     real_connect     = NULL;

void log_sockaddr(const char* tag, const sockaddr* addr) {
    if (!addr) { log_line("  %s: (null)", tag); return; }
    if (addr->sa_family == AF_INET) {
        const sockaddr_in* sin = reinterpret_cast<const sockaddr_in*>(addr);
        unsigned char* ip = (unsigned char*)&sin->sin_addr;
        log_line("  %s: %u.%u.%u.%u:%u",
                 tag, ip[0], ip[1], ip[2], ip[3], ntohs(sin->sin_port));
    } else if (addr->sa_family == AF_INET6) {
        log_line("  %s: AF_INET6 port=%u (ipv6 detail omitted)",
                 tag, ntohs(reinterpret_cast<const sockaddr_in6*>(addr)->sin6_port));
    } else {
        log_line("  %s: family=%d (unknown)", tag, addr->sa_family);
    }
}

void log_wsabufs(LPWSABUF buf, DWORD count) {
    for (DWORD i = 0; i < count && i < 8; i++) {
        char tag[32];
        _snprintf_s(tag, sizeof(tag), _TRUNCATE, "buf[%lu]", i);
        log_payload(tag, reinterpret_cast<const unsigned char*>(buf[i].buf), (int)buf[i].len);
    }
}

int WSAAPI my_WSASendTo(
    SOCKET s, LPWSABUF buf, DWORD count, LPDWORD sent, DWORD flags,
    const sockaddr* to, int tolen,
    LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE cr) {
    DWORD total = 0;
    if (buf) for (DWORD i = 0; i < count; i++) total += buf[i].len;
    log_line("WSASendTo socket=%llu bufs=%lu total=%lu flags=0x%lx overlapped=%p",
             (unsigned long long)s, count, total, flags, ov);
    log_sockaddr("dest", to);
    if (buf) log_wsabufs(buf, count);
    return real_WSASendTo(s, buf, count, sent, flags, to, tolen, ov, cr);
}

int WSAAPI my_WSASend(
    SOCKET s, LPWSABUF buf, DWORD count, LPDWORD sent, DWORD flags,
    LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE cr) {
    DWORD total = 0;
    if (buf) for (DWORD i = 0; i < count; i++) total += buf[i].len;
    log_line("WSASend   socket=%llu bufs=%lu total=%lu flags=0x%lx overlapped=%p",
             (unsigned long long)s, count, total, flags, ov);
    if (buf) log_wsabufs(buf, count);
    return real_WSASend(s, buf, count, sent, flags, ov, cr);
}

int WSAAPI my_WSARecvFrom(
    SOCKET s, LPWSABUF buf, DWORD count, LPDWORD recvd, LPDWORD flags,
    sockaddr* from, LPINT fromlen,
    LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE cr) {
    int rc = real_WSARecvFrom(s, buf, count, recvd, flags, from, fromlen, ov, cr);
    log_line("WSARecvFrom socket=%llu rc=%d recvd=%lu",
             (unsigned long long)s, rc, recvd ? *recvd : 0);
    if (rc == 0 && from) log_sockaddr("from", from);
    if (rc == 0 && buf && recvd && *recvd > 0) {
        DWORD remaining = *recvd;
        for (DWORD i = 0; i < count && remaining > 0 && i < 8; i++) {
            DWORD take = buf[i].len < remaining ? buf[i].len : remaining;
            char tag[32];
            _snprintf_s(tag, sizeof(tag), _TRUNCATE, "buf[%lu]", i);
            log_payload(tag, reinterpret_cast<const unsigned char*>(buf[i].buf), (int)take);
            remaining -= take;
        }
    }
    return rc;
}

int WSAAPI my_WSARecv(
    SOCKET s, LPWSABUF buf, DWORD count, LPDWORD recvd, LPDWORD flags,
    LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE cr) {
    int rc = real_WSARecv(s, buf, count, recvd, flags, ov, cr);
    log_line("WSARecv     socket=%llu rc=%d recvd=%lu",
             (unsigned long long)s, rc, recvd ? *recvd : 0);
    if (rc == 0 && buf && recvd && *recvd > 0) {
        DWORD remaining = *recvd;
        for (DWORD i = 0; i < count && remaining > 0 && i < 8; i++) {
            DWORD take = buf[i].len < remaining ? buf[i].len : remaining;
            char tag[32];
            _snprintf_s(tag, sizeof(tag), _TRUNCATE, "buf[%lu]", i);
            log_payload(tag, reinterpret_cast<const unsigned char*>(buf[i].buf), (int)take);
            remaining -= take;
        }
    }
    return rc;
}

SOCKET WSAAPI my_WSASocketW(int af, int type, int protocol,
                            LPWSAPROTOCOL_INFOW info, GROUP grp, DWORD flags) {
    SOCKET s = real_WSASocketW(af, type, protocol, info, grp, flags);
    log_line("WSASocketW af=%d type=%d proto=%d flags=0x%lx -> socket=%llu",
             af, type, protocol, flags, (unsigned long long)s);
    return s;
}

int WSAAPI my_sendto(SOCKET s, const char* buf, int len, int flags,
                     const sockaddr* to, int tolen) {
    log_line("sendto    socket=%llu len=%d flags=0x%x",
             (unsigned long long)s, len, flags);
    log_sockaddr("dest", to);
    if (buf && len > 0) log_payload("buf", reinterpret_cast<const unsigned char*>(buf), len);
    return real_sendto(s, buf, len, flags, to, tolen);
}

int WSAAPI my_send(SOCKET s, const char* buf, int len, int flags) {
    log_line("send      socket=%llu len=%d flags=0x%x",
             (unsigned long long)s, len, flags);
    if (buf && len > 0) log_payload("buf", reinterpret_cast<const unsigned char*>(buf), len);
    return real_send(s, buf, len, flags);
}

int WSAAPI my_recvfrom(SOCKET s, char* buf, int len, int flags,
                       sockaddr* from, int* fromlen) {
    int rc = real_recvfrom(s, buf, len, flags, from, fromlen);
    log_line("recvfrom  socket=%llu rc=%d", (unsigned long long)s, rc);
    if (rc > 0 && from) log_sockaddr("from", from);
    if (rc > 0 && buf) log_payload("buf", reinterpret_cast<const unsigned char*>(buf), rc);
    return rc;
}

int WSAAPI my_recv(SOCKET s, char* buf, int len, int flags) {
    int rc = real_recv(s, buf, len, flags);
    log_line("recv      socket=%llu rc=%d", (unsigned long long)s, rc);
    if (rc > 0 && buf) log_payload("buf", reinterpret_cast<const unsigned char*>(buf), rc);
    return rc;
}

int WSAAPI my_bind(SOCKET s, const sockaddr* addr, int addrlen) {
    int rc = real_bind(s, addr, addrlen);
    log_line("bind      socket=%llu rc=%d", (unsigned long long)s, rc);
    log_sockaddr("addr", addr);
    return rc;
}

int WSAAPI my_connect(SOCKET s, const sockaddr* addr, int addrlen) {
    log_line("connect   socket=%llu", (unsigned long long)s);
    log_sockaddr("addr", addr);
    return real_connect(s, addr, addrlen);
}

}

struct hook_entry {
    const char* name;
    void*       hook;
    void**      original;
};

void install_wsa_hooks(HMODULE target_module, const wchar_t* module_name) {
    const wchar_t* label = module_name ? module_name : L"(unknown)";
    log_line("Hooking WS2_32 imports in %S (base=%p):", label, target_module);

    hook_entry entries[] = {
        {"WSASendTo",   (void*)my_WSASendTo,   (void**)&real_WSASendTo  },
        {"WSASend",     (void*)my_WSASend,     (void**)&real_WSASend    },
        {"WSARecvFrom", (void*)my_WSARecvFrom, (void**)&real_WSARecvFrom},
        {"WSARecv",     (void*)my_WSARecv,     (void**)&real_WSARecv    },
        {"WSASocketW",  (void*)my_WSASocketW,  (void**)&real_WSASocketW },
        {"sendto",      (void*)my_sendto,      (void**)&real_sendto     },
        {"send",        (void*)my_send,        (void**)&real_send       },
        {"recvfrom",    (void*)my_recvfrom,    (void**)&real_recvfrom   },
        {"recv",        (void*)my_recv,        (void**)&real_recv       },
        {"bind",        (void*)my_bind,        (void**)&real_bind       },
        {"connect",     (void*)my_connect,     (void**)&real_connect    },
    };

    int hooked = 0;
    for (size_t i = 0; i < sizeof(entries) / sizeof(entries[0]); i++) {
        void* prev = hook_iat(target_module, "WS2_32.dll", entries[i].name, entries[i].hook);
        if (prev) {
            if (!*entries[i].original) *entries[i].original = prev;
            log_line("  %-12s -> hooked   prev=%p", entries[i].name, prev);
            hooked++;
        }
    }
    if (hooked == 0) {
        log_line("  (no WS2_32 imports found in this module)");
    } else {
        log_line("  %d WS2_32 import(s) hooked.", hooked);
    }
}
