using System.Security.Cryptography;

namespace TerkirdoComputer.Rest;

public sealed class TokenManager
{
    private readonly bool _persistMetadata;

    public string Token { get; }
    public string TokenFilePath { get; }
    public string PortFilePath { get; }
    public string UrlFilePath { get; }
    public string JsonFilePath { get; }
    public int ActivePort { get; private set; }
    public bool PersistMetadata => _persistMetadata;

    public TokenManager(string? overrideToken = null, int port = 5005, bool persistMetadata = true)
    {
        _persistMetadata = persistMetadata;

        string? envHome = Environment.GetEnvironmentVariable("TERKIRDO_HOME");
        string terkirdoDir = !string.IsNullOrWhiteSpace(envHome)
            ? Path.GetFullPath(envHome.Trim())
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".terkirdo");

        TokenFilePath = Path.Combine(terkirdoDir, "serve.token");
        PortFilePath = Path.Combine(terkirdoDir, "serve.port");
        UrlFilePath = Path.Combine(terkirdoDir, "serve.url");
        JsonFilePath = Path.Combine(terkirdoDir, "serve.json");
        ActivePort = port;

        if (!string.IsNullOrWhiteSpace(overrideToken))
        {
            Token = overrideToken.Trim();
        }
        else
        {
            // Generate cryptographically secure 256-bit random hex token (64 hex characters)
            byte[] bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            Token = Convert.ToHexString(bytes).ToLowerInvariant();
        }

        if (_persistMetadata)
        {
            SaveMetadata(port);
        }
    }

    public void SaveMetadata(int port)
    {
        ActivePort = port;
        if (!_persistMetadata)
        {
            return;
        }

        try
        {
            string? dir = Path.GetDirectoryName(TokenFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(TokenFilePath, Token);
            File.WriteAllText(PortFilePath, port.ToString());
            File.WriteAllText(UrlFilePath, $"http://127.0.0.1:{port}");

            var meta = new
            {
                service = "TerkirdoComputer",
                activePort = port,
                activeUrl = $"http://127.0.0.1:{port}",
                discoveryPort = 5005,
                token = Token,
                updatedAt = DateTime.UtcNow
            };
            File.WriteAllText(JsonFilePath, System.Text.Json.JsonSerializer.Serialize(meta, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TerkirdoComputer Rest] Warning: Could not write serve metadata files: {ex.Message}");
        }
    }

    public bool Validate(string? authorizationHeader, string? customTokenHeader = null)
    {
        if (!string.IsNullOrWhiteSpace(customTokenHeader))
        {
            if (CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(customTokenHeader.Trim()),
                System.Text.Encoding.UTF8.GetBytes(Token)))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(authorizationHeader))
        {
            string trimmed = authorizationHeader.Trim();
            if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                string candidate = trimmed["Bearer ".Length..].Trim();
                if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(candidate),
                    System.Text.Encoding.UTF8.GetBytes(Token)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
