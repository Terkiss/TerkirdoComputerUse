using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TerkirdoComputer.Core;

public sealed class ImageCaptureConfig
{
    public const int MinimumDimension = 64;

    [JsonPropertyName("format")]
    public string Format { get; set; } = "png";

    [JsonPropertyName("jpegQuality")]
    public double JpegQuality { get; set; } = 0.78;

    [JsonPropertyName("maxDimension")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxDimension { get; set; }

    [JsonPropertyName("scaleDownAfterMaxSize")]
    public bool ScaleDownAfterMaxSize { get; set; }

    [JsonPropertyName("discardBelowPixelCount")]
    public int DiscardBelowPixelCount { get; set; }

    [JsonPropertyName("captureTimeout")]
    public double CaptureTimeout { get; set; } = 2.0;

    public string MimeType => Format.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => "image/jpeg",
        _ => "image/png"
    };
}

public static class OcuConfig
{
    private static readonly object ConfigCacheLock = new();
    private static string? _cachedPath;
    private static bool _cachedExists;
    private static long _cachedLastWriteUtcTicks;
    private static long _cachedLength;
    private static JsonObject? _cachedJsonObject;
    private static bool _hasCacheEntry;

    private static readonly string[] ValidKeys =
    [
        "virtualDisplay.enabled",
        "image.format",
        "image.jpegQuality",
        "image.maxDimension",
        "image.scaleDownAfterMaxSize",
        "image.discardBelowPixelCount",
        "image.captureTimeout",
        "input.humanize",
        "input.humanizeSeed",
        "vision.waitPollHz",
        "vision.waitTimeoutMs",
        "stream.format",
        "stream.maxFps"
    ];

    public static string GetConfigFilePath()
    {
        string? envOverride = Environment.GetEnvironmentVariable("OPEN_COMPUTER_USE_CONFIG_FILE");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            return Path.GetFullPath(envOverride.Trim());
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".open-computer-use", "config.json");
    }

    public static ImageCaptureConfig LoadImageCaptureConfig()
    {
        var values = GetEffectiveValues();
        var cfg = new ImageCaptureConfig
        {
            Format = values.TryGetValue("image.format", out var fmt) && fmt is string sFmt ? sFmt : "png",
            JpegQuality = values.TryGetValue("image.jpegQuality", out var q) && q is double dQ ? dQ : 0.78,
            MaxDimension = values.TryGetValue("image.maxDimension", out var md) && md is int iMd ? iMd : null,
            ScaleDownAfterMaxSize = values.TryGetValue("image.scaleDownAfterMaxSize", out var sd) && sd is bool bSd && bSd,
            DiscardBelowPixelCount = values.TryGetValue("image.discardBelowPixelCount", out var db) && db is int iDb ? iDb : 0,
            CaptureTimeout = values.TryGetValue("image.captureTimeout", out var ct) && ct is double dCt ? dCt : 2.0
        };
        return cfg;
    }

    public static Dictionary<string, object?> GetEffectiveValues()
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["virtualDisplay.enabled"] = false,
            ["image.format"] = "png",
            ["image.jpegQuality"] = 0.78,
            ["image.maxDimension"] = null,
            ["image.scaleDownAfterMaxSize"] = false,
            ["image.discardBelowPixelCount"] = 0,
            ["image.captureTimeout"] = 2.0,
            ["input.humanize"] = "none",
            ["input.humanizeSeed"] = null,
            ["vision.waitPollHz"] = 120,
            ["vision.waitTimeoutMs"] = 5000,
            ["stream.format"] = "jpg",
            ["stream.maxFps"] = 30
        };

        var fileNode = ReadConfigFileObject();
        if (fileNode != null)
        {
            if (fileNode["virtualDisplay"] is JsonObject vdObj && vdObj["enabled"] != null)
            {
                if (TryParseBool(vdObj["enabled"]!.ToString(), out bool b))
                {
                    result["virtualDisplay.enabled"] = b;
                }
            }

            if (fileNode["image"] is JsonObject imgObj)
            {
                if (imgObj["format"] != null && TryNormalizeFormat(imgObj["format"]!.ToString(), out string fmt))
                {
                    result["image.format"] = fmt;
                }
                if (imgObj["jpegQuality"] != null && TryNormalizeQuality(imgObj["jpegQuality"]!.ToString(), out double q))
                {
                    result["image.jpegQuality"] = q;
                }
                if (imgObj["maxDimension"] != null && TryNormalizeMaxDimension(imgObj["maxDimension"]!.ToString(), out int? md))
                {
                    result["image.maxDimension"] = md;
                }
                else if (imgObj["maxLongEdgePixels"] != null && TryNormalizeMaxDimension(imgObj["maxLongEdgePixels"]!.ToString(), out int? mdLegacy))
                {
                    result["image.maxDimension"] = mdLegacy;
                }
                if (imgObj["scaleDownAfterMaxSize"] != null && TryParseBool(imgObj["scaleDownAfterMaxSize"]!.ToString(), out bool sd))
                {
                    result["image.scaleDownAfterMaxSize"] = sd;
                }
                if (imgObj["discardBelowPixelCount"] != null && TryNormalizeNonNegativeInt(imgObj["discardBelowPixelCount"]!.ToString(), out int db))
                {
                    result["image.discardBelowPixelCount"] = db;
                }
                if (imgObj["captureTimeout"] != null && TryNormalizePositiveDouble(imgObj["captureTimeout"]!.ToString(), out double ct))
                {
                    result["image.captureTimeout"] = ct;
                }
            }

            if (fileNode["input"] is JsonObject inputObj)
            {
                if (inputObj["humanize"] != null && TryNormalizeHumanize(inputObj["humanize"]!.ToString(), out string hum))
                {
                    result["input.humanize"] = hum;
                }
                if (inputObj["humanizeSeed"] != null && TryNormalizeNullableInt(inputObj["humanizeSeed"]!.ToString(), out int? seed))
                {
                    result["input.humanizeSeed"] = seed;
                }
            }

            if (fileNode["vision"] is JsonObject visObj)
            {
                if (visObj["waitPollHz"] != null && TryNormalizeBoundedInt(visObj["waitPollHz"]!.ToString(), 1, 240, out int pollHz))
                {
                    result["vision.waitPollHz"] = pollHz;
                }
                if (visObj["waitTimeoutMs"] != null && TryNormalizeBoundedInt(visObj["waitTimeoutMs"]!.ToString(), 10, 60000, out int waitTo))
                {
                    result["vision.waitTimeoutMs"] = waitTo;
                }
            }

            if (fileNode["stream"] is JsonObject streamObj)
            {
                if (streamObj["format"] != null && TryNormalizeStreamFormat(streamObj["format"]!.ToString(), out string sFmt))
                {
                    result["stream.format"] = sFmt;
                }
                if (streamObj["maxFps"] != null && TryNormalizeBoundedInt(streamObj["maxFps"]!.ToString(), 1, 120, out int sFps))
                {
                    result["stream.maxFps"] = sFps;
                }
            }
        }

        // Apply environment overrides
        string? envVd = Environment.GetEnvironmentVariable("OPEN_COMPUTER_USE_VIRTUAL_DISPLAY_ENABLED");
        if (!string.IsNullOrWhiteSpace(envVd) && TryParseBool(envVd, out bool bEnvVd))
        {
            result["virtualDisplay.enabled"] = bEnvVd;
        }

        string? envFmt = Environment.GetEnvironmentVariable("OPEN_COMPUTER_USE_IMAGE_FORMAT");
        if (!string.IsNullOrWhiteSpace(envFmt) && TryNormalizeFormat(envFmt, out string sEnvFmt))
        {
            result["image.format"] = sEnvFmt;
        }

        string? envQ = Environment.GetEnvironmentVariable("OPEN_COMPUTER_USE_IMAGE_JPEG_QUALITY");
        if (!string.IsNullOrWhiteSpace(envQ) && TryNormalizeQuality(envQ, out double dEnvQ))
        {
            result["image.jpegQuality"] = dEnvQ;
        }

        string? envMd = Environment.GetEnvironmentVariable("OPEN_COMPUTER_USE_IMAGE_MAX_DIMENSION");
        if (!string.IsNullOrWhiteSpace(envMd) && TryNormalizeMaxDimension(envMd, out int? iEnvMd))
        {
            result["image.maxDimension"] = iEnvMd;
        }

        string? envSd = Environment.GetEnvironmentVariable("OPEN_COMPUTER_USE_IMAGE_SCALE_DOWN_AFTER_MAX_SIZE");
        if (!string.IsNullOrWhiteSpace(envSd) && TryParseBool(envSd, out bool bEnvSd))
        {
            result["image.scaleDownAfterMaxSize"] = bEnvSd;
        }

        string? envDb = Environment.GetEnvironmentVariable("OPEN_COMPUTER_USE_IMAGE_DISCARD_BELOW_PIXEL_COUNT");
        if (!string.IsNullOrWhiteSpace(envDb) && TryNormalizeNonNegativeInt(envDb, out int iEnvDb))
        {
            result["image.discardBelowPixelCount"] = iEnvDb;
        }

        string? envCt = Environment.GetEnvironmentVariable("OPEN_COMPUTER_USE_IMAGE_CAPTURE_TIMEOUT");
        if (!string.IsNullOrWhiteSpace(envCt) && TryNormalizePositiveDouble(envCt, out double dEnvCt))
        {
            result["image.captureTimeout"] = dEnvCt;
        }

        string? envHum = Environment.GetEnvironmentVariable("TERKIRDO_INPUT_HUMANIZE")
                         ?? Environment.GetEnvironmentVariable("OPEN_COMPUTER_USE_INPUT_HUMANIZE");
        if (!string.IsNullOrWhiteSpace(envHum) && TryNormalizeHumanize(envHum, out string sEnvHum))
        {
            result["input.humanize"] = sEnvHum;
        }

        string? envSeed = Environment.GetEnvironmentVariable("TERKIRDO_INPUT_HUMANIZE_SEED")
                          ?? Environment.GetEnvironmentVariable("OPEN_COMPUTER_USE_INPUT_HUMANIZE_SEED");
        if (!string.IsNullOrWhiteSpace(envSeed) && TryNormalizeNullableInt(envSeed, out int? iEnvSeed))
        {
            result["input.humanizeSeed"] = iEnvSeed;
        }

        string? envPollHz = Environment.GetEnvironmentVariable("TERKIRDO_VISION_WAIT_POLL_HZ")
                            ?? Environment.GetEnvironmentVariable("OPEN_COMPUTER_USE_VISION_WAIT_POLL_HZ");
        if (!string.IsNullOrWhiteSpace(envPollHz) && TryNormalizeBoundedInt(envPollHz, 1, 240, out int iEnvPollHz))
        {
            result["vision.waitPollHz"] = iEnvPollHz;
        }

        string? envWaitTo = Environment.GetEnvironmentVariable("TERKIRDO_VISION_WAIT_TIMEOUT_MS")
                            ?? Environment.GetEnvironmentVariable("OPEN_COMPUTER_USE_VISION_WAIT_TIMEOUT_MS");
        if (!string.IsNullOrWhiteSpace(envWaitTo) && TryNormalizeBoundedInt(envWaitTo, 10, 60000, out int iEnvWaitTo))
        {
            result["vision.waitTimeoutMs"] = iEnvWaitTo;
        }

        string? envStreamFmt = Environment.GetEnvironmentVariable("TERKIRDO_STREAM_FORMAT")
                               ?? Environment.GetEnvironmentVariable("OPEN_COMPUTER_USE_STREAM_FORMAT");
        if (!string.IsNullOrWhiteSpace(envStreamFmt) && TryNormalizeStreamFormat(envStreamFmt, out string sEnvStreamFmt))
        {
            result["stream.format"] = sEnvStreamFmt;
        }

        string? envStreamFps = Environment.GetEnvironmentVariable("TERKIRDO_STREAM_MAX_FPS")
                               ?? Environment.GetEnvironmentVariable("OPEN_COMPUTER_USE_STREAM_MAX_FPS");
        if (!string.IsNullOrWhiteSpace(envStreamFps) && TryNormalizeBoundedInt(envStreamFps, 1, 120, out int iEnvStreamFps))
        {
            result["stream.maxFps"] = iEnvStreamFps;
        }

        return result;
    }

    public static bool IsValidKey(string key) => ValidKeys.Contains(key, StringComparer.Ordinal);

    public static object? GetValue(string key)
    {
        if (!IsValidKey(key))
        {
            throw new ArgumentException($"Unknown config key '{key}'. Supported keys: {string.Join(", ", ValidKeys)}");
        }
        return GetEffectiveValues()[key];
    }

    public static object? SetValue(string key, string rawValue)
    {
        if (!IsValidKey(key))
        {
            throw new ArgumentException($"Unknown config key '{key}'. Supported keys: {string.Join(", ", ValidKeys)}");
        }

        object? normalized = NormalizeForKey(key, rawValue);
        var root = ReadConfigFileObject(cloneForMutation: true) ?? new JsonObject();

        string[] parts = key.Split('.', 2);
        string section = parts[0];
        string prop = parts[1];

        if (root[section] is not JsonObject secObj)
        {
            secObj = new JsonObject();
            root[section] = secObj;
        }

        if (normalized is null)
        {
            secObj.Remove(prop);
        }
        else if (normalized is bool b)
        {
            secObj[prop] = b;
        }
        else if (normalized is int i)
        {
            secObj[prop] = i;
        }
        else if (normalized is double d)
        {
            secObj[prop] = d;
        }
        else
        {
            secObj[prop] = normalized.ToString();
        }

        WriteConfigFileObject(root);
        return normalized;
    }

    public static void Reset(string? key = null)
    {
        string path = GetConfigFilePath();
        if (string.IsNullOrWhiteSpace(key))
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            InvalidateConfigCache();
            return;
        }

        if (!IsValidKey(key))
        {
            throw new ArgumentException($"Unknown config key '{key}'. Supported keys: {string.Join(", ", ValidKeys)}");
        }

        var root = ReadConfigFileObject(cloneForMutation: true);
        if (root == null)
        {
            return;
        }

        string[] parts = key.Split('.', 2);
        if (root[parts[0]] is JsonObject secObj)
        {
            secObj.Remove(parts[1]);
            if (secObj.Count == 0)
            {
                root.Remove(parts[0]);
            }
        }

        WriteConfigFileObject(root);
    }

    public static string FormatValue(object? val)
    {
        if (val is null) return "null";
        if (val is bool b) return b ? "true" : "false";
        if (val is double d) return d.ToString("0.##", CultureInfo.InvariantCulture);
        return Convert.ToString(val, CultureInfo.InvariantCulture) ?? "null";
    }

    private static object? NormalizeForKey(string key, string rawValue)
    {
        return key switch
        {
            "virtualDisplay.enabled" or "image.scaleDownAfterMaxSize" =>
                TryParseBool(rawValue, out bool b)
                    ? b
                    : throw new ArgumentException($"Config key '{key}' requires a boolean (true or false)."),
            "image.format" =>
                TryNormalizeFormat(rawValue, out string fmt)
                    ? fmt
                    : throw new ArgumentException("Config key 'image.format' must be one of: png, jpg."),
            "image.jpegQuality" =>
                TryNormalizeQuality(rawValue, out double q)
                    ? q
                    : throw new ArgumentException("Config key 'image.jpegQuality' must be a number between 0.0 and 1.0 (or 1..100)."),
            "image.maxDimension" =>
                TryNormalizeMaxDimension(rawValue, out int? md)
                    ? md
                    : throw new ArgumentException($"Config key 'image.maxDimension' must be null/none or an integer >= {ImageCaptureConfig.MinimumDimension}."),
            "image.discardBelowPixelCount" =>
                TryNormalizeNonNegativeInt(rawValue, out int db)
                    ? db
                    : throw new ArgumentException("Config key 'image.discardBelowPixelCount' must be an integer >= 0."),
            "image.captureTimeout" =>
                TryNormalizePositiveDouble(rawValue, out double ct)
                    ? ct
                    : throw new ArgumentException("Config key 'image.captureTimeout' must be a number > 0."),
            "input.humanize" =>
                TryNormalizeHumanize(rawValue, out string hum)
                    ? hum
                    : throw new ArgumentException("Config key 'input.humanize' must be one of: none, subtle, natural, fast (or true/false)."),
            "input.humanizeSeed" =>
                TryNormalizeNullableInt(rawValue, out int? seed)
                    ? seed
                    : throw new ArgumentException("Config key 'input.humanizeSeed' must be null/none or a valid 32-bit integer."),
            "vision.waitPollHz" =>
                TryNormalizeBoundedInt(rawValue, 1, 240, out int pollHz)
                    ? pollHz
                    : throw new ArgumentException("Config key 'vision.waitPollHz' must be an integer between 1 and 240."),
            "vision.waitTimeoutMs" =>
                TryNormalizeBoundedInt(rawValue, 10, 60000, out int waitTo)
                    ? waitTo
                    : throw new ArgumentException("Config key 'vision.waitTimeoutMs' must be an integer between 10 and 60000."),
            "stream.format" =>
                TryNormalizeStreamFormat(rawValue, out string sFmt)
                    ? sFmt
                    : throw new ArgumentException("Config key 'stream.format' must be one of: bgra32, jpg, png."),
            "stream.maxFps" =>
                TryNormalizeBoundedInt(rawValue, 1, 120, out int sFps)
                    ? sFps
                    : throw new ArgumentException("Config key 'stream.maxFps' must be an integer between 1 and 120."),
            _ => throw new ArgumentException($"Unknown config key '{key}'.")
        };
    }

    public static bool TryNormalizeHumanize(string? raw, out string normalized)
    {
        normalized = "none";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        switch (raw.Trim().ToLowerInvariant())
        {
            case "none" or "false" or "0" or "off":
                normalized = "none";
                return true;
            case "subtle":
                normalized = "subtle";
                return true;
            case "natural" or "true" or "1" or "on":
                normalized = "natural";
                return true;
            case "fast":
                normalized = "fast";
                return true;
            default:
                return false;
        }
    }

    public static bool TryNormalizeStreamFormat(string? raw, out string format)
    {
        format = "jpg";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        switch (raw.Trim().ToLowerInvariant())
        {
            case "bgra32" or "bgra" or "raw":
                format = "bgra32";
                return true;
            case "jpg" or "jpeg":
                format = "jpg";
                return true;
            case "png":
                format = "png";
                return true;
            default:
                return false;
        }
    }

    private static bool TryNormalizeNullableInt(string? raw, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string trimmed = raw.Trim().ToLowerInvariant();
        if (trimmed is "null" or "none" or "off")
        {
            value = null;
            return true;
        }
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            value = parsed;
            return true;
        }
        return false;
    }

    private static bool TryNormalizeBoundedInt(string? raw, int min, int max, out int value)
    {
        value = min;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
            parsed >= min && parsed <= max)
        {
            value = parsed;
            return true;
        }
        return false;
    }

    private static bool TryParseBool(string? raw, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        switch (raw.Trim().ToLowerInvariant())
        {
            case "1" or "true" or "yes" or "on":
                value = true;
                return true;
            case "0" or "false" or "no" or "off":
                value = false;
                return true;
            default:
                return false;
        }
    }

    private static bool TryNormalizeFormat(string? raw, out string format)
    {
        format = "png";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        switch (raw.Trim().ToLowerInvariant())
        {
            case "png":
                format = "png";
                return true;
            case "jpg" or "jpeg":
                format = "jpg";
                return true;
            default:
                return false;
        }
    }

    private static bool TryNormalizeQuality(string? raw, out double quality)
    {
        quality = 0.78;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed))
        {
            return false;
        }
        if (parsed > 1.0 && parsed <= 100.0)
        {
            parsed /= 100.0;
        }
        if (parsed < 0.0 || parsed > 1.0)
        {
            return false;
        }
        quality = Math.Round(parsed, 4);
        return true;
    }

    private static bool TryNormalizeMaxDimension(string? raw, out int? maxDimension)
    {
        maxDimension = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string trimmed = raw.Trim().ToLowerInvariant();
        if (trimmed is "null" or "none" or "0" or "off")
        {
            maxDimension = null;
            return true;
        }
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
            parsed >= ImageCaptureConfig.MinimumDimension)
        {
            maxDimension = parsed;
            return true;
        }
        return false;
    }

    private static bool TryNormalizeNonNegativeInt(string? raw, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed >= 0)
        {
            value = parsed;
            return true;
        }
        return false;
    }

    private static bool TryNormalizePositiveDouble(string? raw, out double value)
    {
        value = 2.0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
            double.IsFinite(parsed) && parsed > 0)
        {
            value = parsed;
            return true;
        }
        return false;
    }

    private static void InvalidateConfigCache()
    {
        lock (ConfigCacheLock)
        {
            _hasCacheEntry = false;
            _cachedPath = null;
            _cachedExists = false;
            _cachedLastWriteUtcTicks = 0;
            _cachedLength = 0;
            _cachedJsonObject = null;
        }
    }

    private static JsonObject? ReadConfigFileObject(bool cloneForMutation = false)
    {
        string path = GetConfigFilePath();
        var fi = new FileInfo(path);
        bool exists = fi.Exists;
        long writeTicks = exists ? fi.LastWriteTimeUtc.Ticks : 0;
        long length = exists ? fi.Length : 0;

        lock (ConfigCacheLock)
        {
            if (_hasCacheEntry &&
                string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase) &&
                _cachedExists == exists &&
                _cachedLastWriteUtcTicks == writeTicks &&
                _cachedLength == length)
            {
                return cloneForMutation
                    ? (_cachedJsonObject?.DeepClone() as JsonObject)
                    : _cachedJsonObject;
            }
        }

        JsonObject? parsed = null;
        if (exists)
        {
            try
            {
                string json = File.ReadAllText(path);
                parsed = JsonNode.Parse(json) as JsonObject;
            }
            catch
            {
                parsed = null;
            }
        }

        lock (ConfigCacheLock)
        {
            _hasCacheEntry = true;
            _cachedPath = path;
            _cachedExists = exists;
            _cachedLastWriteUtcTicks = writeTicks;
            _cachedLength = length;
            _cachedJsonObject = parsed;
        }

        return cloneForMutation ? (parsed?.DeepClone() as JsonObject) : parsed;
    }

    private static void WriteConfigFileObject(JsonObject obj)
    {
        string path = GetConfigFilePath();
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, obj.ToJsonString(options));
        InvalidateConfigCache();
    }
}
