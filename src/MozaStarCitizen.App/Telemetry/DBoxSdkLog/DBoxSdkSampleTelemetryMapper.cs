namespace MozaStarCitizen.App.Telemetry.DBoxSdkLog;

public sealed class DBoxSdkSampleTelemetryMapper
{
    private const double StandardGravity = 9.80665;

    private const int EventEngineStop = 4;
    private const int EventEngineBoostStart = 5;
    private const int EventEngineBoostStop = 6;
    private const int EventImpact = 7;

    private const int FieldEngineRpm = 1;
    private const int FieldEngineRpmMax = 2;
    private const int FieldAccelerationXyz = 8;
    private const int FieldEventIntensity = 24;
    private const int FieldEngineBoost = 33;
    private const int FieldActorGForceXyz = 68;
    private const int FieldLandingGearDeployment = 92;
    private const int FieldEngine1N1 = 155;

    private readonly Dictionary<uint, EventSchema> _schemas = [];
    private readonly HashSet<uint> _invalidSchemas = [];
    private DBoxSdkVector? _actorGForce;
    private DBoxSdkVector? _acceleration;
    private double? _engineRpm;
    private double? _engineRpmMax;
    private double? _engineN1;
    private double _boost;
    private double _landingGear;
    private DateTimeOffset? _runAnchor;
    private double _runStartElapsedMilliseconds;

    public string? AppKey { get; private set; }

    public int? AppBuild { get; private set; }

    public string? ApiKey { get; private set; }

    public bool IsSupportedSample { get; private set; }

    public bool HasTerminated { get; private set; }

    public long RegisteredSchemaCount { get; private set; }

    public long PostCount { get; private set; }

    public long MappedFieldCount { get; private set; }

    public long UnmappedFieldCount { get; private set; }

    public long ValidationFailureCount { get; private set; }

    public bool TryApply(
        DBoxSdkLogRecord record,
        out StarCitizenTelemetryFrame? frame,
        out string? warning)
    {
        frame = null;
        warning = null;

        switch (record.Method)
        {
            case "Initialize":
                BeginRun(record);
                if (!IsSupportedSample)
                {
                    warning = $"Unsupported AppKey '{AppKey ?? "(missing)"}'. Only official SampleRacer and SampleFlyer logs are accepted.";
                }

                return false;

            case "RegisterEvent":
                return RegisterSchema(record, out warning);

            case "Stop":
                ClearDynamicState();
                if (IsSupportedSample)
                {
                    frame = CreateFrame(record, impact: 0);
                    return true;
                }

                return false;

            case "ResetState":
            case "Close":
            case "Terminate":
                if (record.Method == "Terminate")
                {
                    HasTerminated = true;
                }

                ClearAllState();
                if (IsSupportedSample)
                {
                    frame = CreateFrame(record, impact: 0);
                    return true;
                }

                return false;
        }

        if (record.Method != "PostEvent")
        {
            return false;
        }

        PostCount++;
        if (!IsSupportedSample)
        {
            warning = "PostEvent was ignored because the log is not from an allowed SDK sample.";
            return false;
        }

        if (HasTerminated)
        {
            ValidationFailureCount++;
            warning = "PostEvent was ignored because the run has already terminated.";
            return false;
        }

        if (record.EventKey is not { } key ||
            !_schemas.TryGetValue(key, out var schema) ||
            _invalidSchemas.Contains(key))
        {
            ValidationFailureCount++;
            warning = $"PostEvent key {record.EventKey?.ToString() ?? "(missing)"} has no valid registered schema.";
            return false;
        }

        if (schema.Fields.Count != record.Values.Count)
        {
            ValidationFailureCount++;
            warning = $"PostEvent key {key} has {record.Values.Count} value(s), but its schema has {schema.Fields.Count}.";
            return false;
        }

        if (record.DataSize is not { } dataSize || dataSize < 0)
        {
            ValidationFailureCount++;
            warning = $"PostEvent key {key} is missing a valid DataSize.";
            return false;
        }

        var minimumDataSize = 0;
        for (var index = 0; index < schema.Fields.Count; index++)
        {
            var field = schema.Fields[index];
            var value = record.Values[index];
            if (value.TypeId is not { } typeId || typeId != field.TypeId)
            {
                ValidationFailureCount++;
                warning = $"PostEvent key {key} field {index} type " +
                    $"{value.TypeId?.ToString() ?? "(missing)"} does not match registered type {field.TypeId}.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(field.TypeName) &&
                !string.Equals(value.TypeName, field.TypeName, StringComparison.Ordinal))
            {
                ValidationFailureCount++;
                warning = $"PostEvent key {key} field {index} element {value.TypeName} " +
                    $"does not match registered type name {field.TypeName}.";
                return false;
            }

            if (!TryGetFixedTypeSize(typeId, out var fieldSize) || field.Offset < 0)
            {
                ValidationFailureCount++;
                warning = $"PostEvent key {key} field {index} has an unsupported type or offset.";
                return false;
            }

            if (field.Offset > int.MaxValue - fieldSize)
            {
                ValidationFailureCount++;
                warning = $"PostEvent key {key} field {index} offset overflows its layout.";
                return false;
            }

            minimumDataSize = Math.Max(minimumDataSize, field.Offset + fieldSize);
            if (!HasValidValueShape(typeId, value))
            {
                ValidationFailureCount++;
                warning = $"PostEvent key {key} field {index} has an invalid or non-finite value.";
                return false;
            }
        }

        if (dataSize != minimumDataSize)
        {
            ValidationFailureCount++;
            warning = $"PostEvent key {key} DataSize {dataSize} does not match its registered layout ({minimumDataSize}).";
            return false;
        }

        double? eventIntensity = null;
        for (var index = 0; index < schema.Fields.Count; index++)
        {
            var field = schema.Fields[index];
            var value = record.Values[index];
            if (!ApplyField(field, value, ref eventIntensity))
            {
                UnmappedFieldCount++;
            }
            else
            {
                MappedFieldCount++;
            }
        }

        var impact = 0d;
        switch (schema.EventMeaningId)
        {
            case EventEngineStop:
                _engineRpm = 0;
                _engineN1 = 0;
                _boost = 0;
                break;
            case EventEngineBoostStart:
                _boost = Clamp01(eventIntensity ?? 1);
                break;
            case EventEngineBoostStop:
                _boost = 0;
                break;
            case EventImpact:
                impact = Clamp01(eventIntensity ?? 1);
                break;
        }

        frame = CreateFrame(record, impact);
        return true;
    }

    public void ResetForFileReplacement()
    {
        _schemas.Clear();
        _invalidSchemas.Clear();
        AppKey = null;
        AppBuild = null;
        ApiKey = null;
        IsSupportedSample = false;
        HasTerminated = false;
        _runAnchor = null;
        _runStartElapsedMilliseconds = 0;
        ClearAllState();
    }

    private void BeginRun(DBoxSdkLogRecord record)
    {
        ResetForFileReplacement();
        AppKey = record.AppKey;
        AppBuild = record.AppBuild;
        ApiKey = record.ApiKey;
        _runAnchor = DateTimeOffset.UtcNow;
        _runStartElapsedMilliseconds = record.ElapsedMilliseconds;
        IsSupportedSample =
            string.Equals(AppKey, "SampleRacer", StringComparison.Ordinal) ||
            string.Equals(AppKey, "SampleFlyer", StringComparison.Ordinal);
    }

    private bool RegisterSchema(DBoxSdkLogRecord record, out string? warning)
    {
        warning = null;
        if (!IsSupportedSample)
        {
            return false;
        }

        if (record.EventKey is not { } key ||
            record.EventMeaningId is not { } eventMeaningId)
        {
            ValidationFailureCount++;
            warning = "RegisterEvent is missing its key or meaning.";
            return false;
        }

        if (record.DeclaredFieldCount is not { } declaredCount ||
            declaredCount < 0 ||
            declaredCount != record.Fields.Count)
        {
            ValidationFailureCount++;
            _invalidSchemas.Add(key);
            warning = $"RegisterEvent key {key} declares " +
                $"{record.DeclaredFieldCount?.ToString() ?? "(missing)"} fields but contains {record.Fields.Count}.";
            return false;
        }

        foreach (var field in record.Fields)
        {
            if (field.Offset < 0 ||
                field.Flags != 0 ||
                string.IsNullOrWhiteSpace(field.TypeName) ||
                !TryGetFixedTypeSize(field.TypeId, out _) ||
                !string.Equals(
                    field.TypeName,
                    GetExpectedTypeName(field.TypeId),
                    StringComparison.Ordinal) ||
                (IsMappedFieldMeaning(field.MeaningId) &&
                 !IsTypeCompatibleWithMappedMeaning(field.MeaningId, field.TypeId)))
            {
                ValidationFailureCount++;
                _invalidSchemas.Add(key);
                warning = $"RegisterEvent key {key} contains an unsupported field definition.";
                return false;
            }
        }

        var previousEnd = 0;
        foreach (var field in record.Fields.OrderBy(field => field.Offset))
        {
            _ = TryGetFixedTypeSize(field.TypeId, out var size);
            if (field.Offset < previousEnd || field.Offset > int.MaxValue - size)
            {
                ValidationFailureCount++;
                _invalidSchemas.Add(key);
                warning = $"RegisterEvent key {key} contains overlapping or overflowing fields.";
                return false;
            }

            previousEnd = field.Offset + size;
        }

        var schema = new EventSchema(
            eventMeaningId,
            record.EventMeaningName,
            record.Fields.ToArray());

        if (_schemas.TryGetValue(key, out var previous) && !SchemasMatch(previous, schema))
        {
            ValidationFailureCount++;
            _invalidSchemas.Add(key);
            warning = $"RegisterEvent key {key} changed layout within one run.";
            return false;
        }

        _schemas[key] = schema;
        RegisteredSchemaCount = _schemas.Count;
        return false;
    }

    private bool ApplyField(
        DBoxSdkFieldDefinition field,
        DBoxSdkPostedValue value,
        ref double? eventIntensity)
    {
        switch (field.MeaningId)
        {
            case FieldEngineRpm:
                return TryAssignScalar(value, scalar => _engineRpm = Math.Max(0, scalar));
            case FieldEngineRpmMax:
                return TryAssignScalar(value, scalar => _engineRpmMax = Math.Max(0, scalar));
            case FieldAccelerationXyz:
                return TryAssignVector(value, vector => _acceleration = vector);
            case FieldEventIntensity:
                if (!value.TryGetScalar(out var intensity))
                {
                    return false;
                }

                eventIntensity = Clamp01(intensity);
                return true;
            case FieldEngineBoost:
                return TryAssignScalar(value, scalar => _boost = Clamp01(scalar));
            case FieldActorGForceXyz:
                return TryAssignVector(value, vector => _actorGForce = vector);
            case FieldLandingGearDeployment:
                return TryAssignScalar(value, scalar => _landingGear = Clamp01(scalar));
            case FieldEngine1N1:
                return TryAssignScalar(value, scalar =>
                {
                    var normalized = scalar <= 1 ? scalar : scalar / 100;
                    _engineN1 = Clamp01(normalized);
                });
            default:
                return false;
        }
    }

    private StarCitizenTelemetryFrame CreateFrame(DBoxSdkLogRecord record, double impact)
    {
        var gForce = _actorGForce ??
            (_acceleration is { } acceleration
                ? new DBoxSdkVector(
                    acceleration.X / StandardGravity,
                    acceleration.Y / StandardGravity,
                    acceleration.Z / StandardGravity)
                : default);

        var engineRumble = _engineN1 ??
            (_engineRpm is { } rpm && _engineRpmMax is > 0
                ? Clamp01(rpm / _engineRpmMax.Value)
                : 0);
        var engineFrequency = _engineRpm is { } currentRpm
            ? Math.Clamp(currentRpm / 60, 0, 120)
            : 0;

        return new StarCitizenTelemetryFrame
        {
            Timestamp = GetTimestamp(record),
            Source = "D-BOX SDK sample XML replay",
            EngineRumble = engineRumble,
            EngineFrequencyHz = engineFrequency,
            GForceLateral = gForce.X,
            GForceVertical = gForce.Y,
            GForceLongitudinal = gForce.Z,
            Boost = _boost,
            Impact = impact,
            LandingGear = _landingGear,
            RawKind = record.EventKey is { } key
                ? $"{record.Method}:{key}"
                : record.Method
        };
    }

    private void ClearDynamicState()
    {
        _actorGForce = null;
        _acceleration = null;
        _engineRpm = null;
        _engineN1 = null;
        _boost = 0;
        _landingGear = 0;
    }

    private void ClearAllState()
    {
        ClearDynamicState();
        _engineRpmMax = null;
    }

    private DateTimeOffset GetTimestamp(DBoxSdkLogRecord record)
    {
        if (_runAnchor is not { } anchor)
        {
            return DateTimeOffset.UtcNow;
        }

        try
        {
            return anchor.AddMilliseconds(
                record.ElapsedMilliseconds - _runStartElapsedMilliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static bool HasValidValueShape(int typeId, DBoxSdkPostedValue value)
    {
        return typeId switch
        {
            0x17 => HasInt32Attribute(value, "Value"),
            0x18 => HasInt64Attribute(value, "Value"),
            0x19 or 0x1a => value.TryGetScalar(out _),
            0x89 or 0x8a => value.TryGetVector(out _),
            0x97 =>
                HasInt32Attribute(value, "FL") &&
                HasInt32Attribute(value, "FR") &&
                HasInt32Attribute(value, "BL") &&
                HasInt32Attribute(value, "BR"),
            0x99 or 0x9a =>
                HasFiniteAttribute(value, "FL") &&
                HasFiniteAttribute(value, "FR") &&
                HasFiniteAttribute(value, "BL") &&
                HasFiniteAttribute(value, "BR"),
            _ => false
        };
    }

    private static bool HasInt32Attribute(DBoxSdkPostedValue value, string name) =>
        value.Attributes.TryGetValue(name, out var raw) &&
        int.TryParse(
            raw,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out _);

    private static bool HasInt64Attribute(DBoxSdkPostedValue value, string name) =>
        value.Attributes.TryGetValue(name, out var raw) &&
        long.TryParse(
            raw,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out _);

    private static bool IsMappedFieldMeaning(int meaningId) =>
        meaningId is
            FieldEngineRpm or
            FieldEngineRpmMax or
            FieldAccelerationXyz or
            FieldEventIntensity or
            FieldEngineBoost or
            FieldActorGForceXyz or
            FieldLandingGearDeployment or
            FieldEngine1N1;

    private static bool IsTypeCompatibleWithMappedMeaning(int meaningId, int typeId) =>
        meaningId switch
        {
            FieldAccelerationXyz or FieldActorGForceXyz => typeId is 0x89 or 0x8a,
            FieldEngineRpm or
            FieldEngineRpmMax or
            FieldEventIntensity or
            FieldEngineBoost or
            FieldLandingGearDeployment or
            FieldEngine1N1 => typeId is 0x17 or 0x18 or 0x19 or 0x1a,
            _ => true
        };

    private static bool HasFiniteAttribute(DBoxSdkPostedValue value, string name) =>
        value.Attributes.TryGetValue(name, out var raw) &&
        double.TryParse(
            raw,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) &&
        double.IsFinite(parsed);

    private static bool TryGetFixedTypeSize(int typeId, out int size)
    {
        size = typeId switch
        {
            0x17 or 0x19 => 4,
            0x18 or 0x1a => 8,
            0x89 => 12,
            0x8a => 24,
            0x97 or 0x99 => 16,
            0x9a => 32,
            _ => 0
        };
        return size != 0;
    }

    private static string? GetExpectedTypeName(int typeId) =>
        typeId switch
        {
            0x17 => "Int32",
            0x18 => "Int64",
            0x19 => "Float32",
            0x1a => "Float64",
            0x89 => "XyzFloat32",
            0x8a => "XyzFloat64",
            0x97 => "QuadInt32",
            0x99 => "QuadFloat32",
            0x9a => "QuadFloat64",
            _ => null
        };

    private static bool TryAssignScalar(DBoxSdkPostedValue value, Action<double> assign)
    {
        if (!value.TryGetScalar(out var scalar))
        {
            return false;
        }

        assign(scalar);
        return true;
    }

    private static bool TryAssignVector(DBoxSdkPostedValue value, Action<DBoxSdkVector> assign)
    {
        if (!value.TryGetVector(out var vector))
        {
            return false;
        }

        assign(vector);
        return true;
    }

    private static bool SchemasMatch(EventSchema left, EventSchema right)
    {
        if (left.EventMeaningId != right.EventMeaningId ||
            left.Fields.Count != right.Fields.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Fields.Count; index++)
        {
            if (left.Fields[index] != right.Fields[index])
            {
                return false;
            }
        }

        return true;
    }

    private static double Clamp01(double value) =>
        Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1);

    private sealed record EventSchema(
        int EventMeaningId,
        string? EventMeaningName,
        IReadOnlyList<DBoxSdkFieldDefinition> Fields);
}
