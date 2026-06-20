using System.Reflection.PortableExecutable;
using System.Text;
using Iced.Intel;

// Targeted NativeAOT analyzer: find a string, find the RIP-relative instruction(s)
// in executable sections that reference it, locate the enclosing function, and
// disassemble it. Used to read D-BOX's "System generation" classifier.
//
// usage: AotAnalyze <exe> [anchorString]

if (args.Length < 1) { Console.WriteLine("usage: AotAnalyze <exe> [anchorString]"); return; }
var path = args[0];
var bytes = File.ReadAllBytes(path);

ulong imageBase;
var secs = new List<(string Name, long RVA, long VSize, long Ptr, long RawSize, bool Exec)>();
using (var pe = new PEReader(File.OpenRead(path)))
{
    imageBase = pe.PEHeaders.PEHeader.ImageBase;
    foreach (var s in pe.PEHeaders.SectionHeaders)
        secs.Add((s.Name, s.VirtualAddress, s.VirtualSize, s.PointerToRawData, s.SizeOfRawData,
                  (s.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0));
}

long FileToVA(long off)
{
    foreach (var s in secs) if (off >= s.Ptr && off < s.Ptr + s.RawSize) return (long)imageBase + s.RVA + (off - s.Ptr);
    return -1;
}
long VAToFile(long va)
{
    long rva = va - (long)imageBase;
    foreach (var s in secs) if (rva >= s.RVA && rva < s.RVA + s.RawSize) return s.Ptr + (rva - s.RVA);
    return -1;
}

long FindStringVA(string str)
{
    var pat = Encoding.UTF8.GetBytes(str);
    for (int i = 0; i + pat.Length <= bytes.Length; i++)
    {
        bool m = true;
        for (int j = 0; j < pat.Length; j++) if (bytes[i + j] != pat[j]) { m = false; break; }
        if (m) { long va = FileToVA(i); if (va > 0) return va; }
    }
    return -1;
}

List<long> FindXrefs(long targetVA)
{
    var hits = new List<long>();
    foreach (var s in secs)
    {
        if (!s.Exec) continue;
        long end = Math.Min(s.RawSize, s.VSize) - 4;
        for (long i = 0; i < end; i++)
        {
            int disp = BitConverter.ToInt32(bytes, (int)(s.Ptr + i));
            long fieldVA = (long)imageBase + s.RVA + i;
            if (fieldVA + 4 + disp == targetVA) hits.Add(fieldVA);
        }
    }
    return hits;
}

long FindFuncStart(long va)
{
    long off = VAToFile(va);
    for (long o = off; o > off - 8192 && o > 1; o--)
        if (bytes[o] == 0xCC && bytes[o - 1] == 0xCC)
        {
            long p = o + 1;
            while (p < bytes.Length && bytes[p] == 0xCC) p++;
            return FileToVA(p);
        }
    return va - 48;
}

void Disasm(long startVA, int len)
{
    long off = VAToFile(startVA);
    if (off < 0) { Console.WriteLine($"  (no file offset for {startVA:X})"); return; }
    int n = (int)Math.Min(len, bytes.Length - off);
    var dec = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(bytes, (int)off, n), (ulong)startVA);
    var fmt = new NasmFormatter();
    var so = new StringOutput();
    long limit = startVA + n;
    while (dec.IP < (ulong)limit)
    {
        dec.Decode(out var ins);
        fmt.Format(ins, so);
        var raw = "";
        long io = VAToFile((long)ins.IP);
        for (int k = 0; k < ins.Length && k < 8; k++) raw += bytes[io + k].ToString("x2");
        Console.WriteLine($"{ins.IP:X8}  {raw,-18} {so.ToStringAndReset()}");
        if (ins.Code == Code.INVALID) break;
    }
}

// Diagnostics: section table + where the anchor text actually lives.
Console.WriteLine($"imageBase={imageBase:X}");
foreach (var s in secs)
    Console.WriteLine($"sec {s.Name,-9} RVA={s.RVA:X} VSize={s.VSize:X} Ptr={s.Ptr:X} Raw={s.RawSize:X} exec={s.Exec}");
Console.WriteLine();

List<(long off, long va)> FindAll(byte[] pat)
{
    var r = new List<(long, long)>();
    for (int i = 0; i + pat.Length <= bytes.Length; i++)
    {
        bool m = true;
        for (int j = 0; j < pat.Length; j++) if (bytes[i + j] != pat[j]) { m = false; break; }
        if (m) r.Add((i, FileToVA(i)));
    }
    return r;
}
void Report(string label, byte[] pat)
{
    var all = FindAll(pat);
    Console.WriteLine($"[{label}] matches={all.Count}: " +
        string.Join(" ", all.Take(8).Select(t => $"off={t.off:X}/va={(t.va < 0 ? "none" : t.va.ToString("X"))}")));
}
Report("u8 'System generation'", Encoding.UTF8.GetBytes("System generation"));
Report("u8 'generation unknown'", Encoding.UTF8.GetBytes("generation unknown"));
Report("u16 'System generation'", Encoding.Unicode.GetBytes("System generation"));
Report("u8 'commUnitTypeId'", Encoding.UTF8.GetBytes("commUnitTypeId"));
Report("u8 'acmModelId'", Encoding.UTF8.GetBytes("acmModelId"));

// .NET frozen string object layout: [MethodTable*(8)][length(4)][UTF-16 chars]
// so charVA = objBase + 0xC; code references objBase, not the chars.
long AnchorCharVA(string s)
{
    foreach (var t in FindAll(Encoding.Unicode.GetBytes(s))) if (t.va > 0) return t.va;
    return -1;
}
long charVA = AnchorCharVA("System generation unknown");
if (charVA < 0) charVA = AnchorCharVA("System generation");
Console.WriteLine($"\nanchor char VA={charVA:X}");
if (charVA > 0)
{
    int len = BitConverter.ToInt32(bytes, (int)VAToFile(charVA - 4));
    long mt = BitConverter.ToInt64(bytes, (int)VAToFile(charVA - 0xC));
    Console.WriteLine($"  [charVA-4]=len={len}  [charVA-0xC]=MethodTable={mt:X}");
    long objBase = charVA - 0xC;
    // (a) any direct RIP-relative reference into the object/char region
    for (long obj = charVA - 0x18; obj <= charVA; obj += 4)
    {
        var xr = FindXrefs(obj);
        if (xr.Count > 0) Console.WriteLine($"  DIRECT ref to {obj:X} (obj+{obj - objBase:X}): {string.Join(" ", xr.Select(a => a.ToString("X")))}");
    }
    // (b) broad: any 8-byte reference into the object, in ANY section (covers absolute
    //     immediates embedded in code, and pointer slots in data).
    Console.WriteLine("  scanning ALL sections for any 8-byte reference into the object...");
    long loT = charVA - 0x10, hiT = charVA + 2;
    int shown = 0;
    foreach (var s in secs)
    {
        long end = Math.Min(s.RawSize, s.VSize) - 8;
        for (long i = 0; i < end; i++)
        {
            long v = BitConverter.ToInt64(bytes, (int)(s.Ptr + i));
            if (v < loT || v > hiT) continue;
            long here = (long)imageBase + s.RVA + i;
            if (s.Exec)
            {
                long fstart = FindFuncStart(here);
                Console.WriteLine($"  CODE imm @ {here:X} = {v:X} (obj+{v - objBase:X}) in func {fstart:X}");
                if (shown++ < 3) { Console.WriteLine($"\n===== function @ {fstart:X} =====");
                    Disasm(fstart, (int)Math.Clamp(here - fstart + 0x60, 0x100, 0xC00)); }
            }
            else
            {
                var xr = FindXrefs(here);
                Console.WriteLine($"  DATA slot @ {here:X} = {v:X} (obj+{v - objBase:X}) xrefs({xr.Count}): {string.Join(" ", xr.Take(8).Select(a => a.ToString("X")))}");
                foreach (var x in xr.Take(2))
                {
                    long fstart = FindFuncStart(x);
                    Console.WriteLine($"\n===== function @ {fstart:X} (reads slot at {x:X}) =====");
                    Disasm(fstart, (int)Math.Clamp(x - fstart + 0x80, 0x100, 0xC00));
                }
            }
        }
    }

    // 4-byte styles: NativeAOT relative pointers (fieldVA+disp==target) and RVA32 tables.
    Console.WriteLine("\n=== 4-byte reference search to anchor object ===");
    long[] tgts = { charVA, charVA - 0xC, charVA - 8, charVA - 4 };
    int hits = 0;
    foreach (var s in secs)
    {
        long end = Math.Min(s.RawSize, s.VSize) - 4;
        for (long i = 0; i < end && hits < 80; i++)
        {
            int d = BitConverter.ToInt32(bytes, (int)(s.Ptr + i));
            long fieldVA = (long)imageBase + s.RVA + i;
            foreach (var tg in tgts)
            {
                bool rel = fieldVA + d == tg;
                bool rva = (uint)d == (uint)(tg - (long)imageBase);
                if (!rel && !rva) continue;
                hits++;
                var kind = rel ? "REL" : "RVA32";
                Console.WriteLine($"  {kind} @ {fieldVA:X} sec={s.Name} exec={s.Exec} -> {tg:X} (obj+{tg - (charVA - 0xC):X})");
                if (!s.Exec)
                {
                    var xr = FindXrefs(fieldVA);
                    if (xr.Count > 0) Console.WriteLine($"      slot xrefs: {string.Join(" ", xr.Take(6).Select(a => a.ToString("X")))}");
                }
            }
        }
    }
    if (hits == 0) Console.WriteLine("  no 4-byte references found either.");
}
