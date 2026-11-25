using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerkirdoComputer.Cli;

public static class Installers
{
    public static string GetCurrentExecutablePath()
    {
        return Environment.ProcessPath ??
               Process.GetCurrentProcess().MainModule?.FileName ??
               "terkirdocomputer.exe";
    }

    public static int InstallCodexMcp()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dir = Path.Combine(home, ".codex");
        string file = Path.Combine(dir, "config.toml");

        Directory.CreateDirectory(dir);

        string exePath = GetCurrentExecutablePath().Replace("\\", "\\\\");
        string sectionHeader = "[mcp_servers.open-computer-use]";
        string block = $"{sectionHeader}\ncommand = \"{exePath}\"\nargs = [\"mcp\"]\n";

        string existing = File.Exists(file) ? File.ReadAllText(file) : "";

        if (existing.Contains(sectionHeader))
        {
            // Replace existing section
            var lines = existing.Split('\n').ToList();
            int startIndex = -1;
            int endIndex = lines.Count;

            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim() == sectionHeader)
                {
                    startIndex = i;
                    for (int j = i + 1; j < lines.Count; j++)
                    {
                        if (lines[j].Trim().StartsWith("["))
                        {
                            endIndex = j;
                            break;
                        }
                    }
                    break;
                }
            }

            if (startIndex >= 0)
            {
                lines.RemoveRange(startIndex, endIndex - startIndex);
                lines.Insert(startIndex, block.TrimEnd());
                File.WriteAllText(file, string.Join("\n", lines));
            }
            else
            {
                File.AppendAllText(file, "\n" + block);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(existing) && !existing.EndsWith("\n"))
            {
                existing += "\n";
            }
            File.WriteAllText(file, existing + block);
        }

        Console.WriteLine($"Successfully installed open-computer-use to Codex config at: {file}");
        return 0;
    }

    public static int InstallClaudeMcp()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string file = Path.Combine(home, ".claude.json");
        return InstallJsonMcp(file, "Claude");
    }

    public static int InstallGeminiMcp()
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), ".gemini");
        string file = Path.Combine(dir, "settings.json");
        return InstallJsonMcp(file, "Gemini");
    }

    public static int InstallOpencodeMcp()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dir = Path.Combine(home, ".config", "opencode");
        string file = Path.Combine(dir, "opencode.json");
        return InstallJsonMcp(file, "OpenCode");
    }

    private static int InstallJsonMcp(string filePath, string clientName)
    {
        string dir = Path.GetDirectoryName(filePath)!;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        JsonObject root;
        if (File.Exists(filePath))
        {
            try
            {
                string text = File.ReadAllText(filePath);
                root = JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
            }
            catch
            {
                root = new JsonObject();
            }
        }
        else
        {
            root = new JsonObject();
        }

        JsonObject mcpServers;
        if (root.TryGetPropertyValue("mcpServers", out var serversNode) && serversNode is JsonObject sObj)
        {
            mcpServers = sObj;
        }
        else
        {
            mcpServers = new JsonObject();
            root["mcpServers"] = mcpServers;
        }

        string exePath = GetCurrentExecutablePath();
        mcpServers["open-computer-use"] = new JsonObject
        {
            ["command"] = exePath,
            ["args"] = new JsonArray { "mcp" }
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(filePath, root.ToJsonString(options));

        Console.WriteLine($"Successfully installed open-computer-use to {clientName} config at: {filePath}");
        return 0;
    }

    public static string? GetEmbeddedSkillContent()
    {
        var assembly = typeof(Installers).Assembly;
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase));

        if (resourceName != null)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                return reader.ReadToEnd();
            }
        }
        return null;
    }

    public static int InstallSkill(string? customPath = null)
        => InstallSkill(customPath, homeOverride: null);

    public static int InstallSkill(string? customPath, string? homeOverride)
    {
        string home = string.IsNullOrWhiteSpace(homeOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : homeOverride;

        string? content = GetEmbeddedSkillContent();

        if (string.IsNullOrWhiteSpace(content))
        {
            string[] candidates =
            [
                Path.Combine(Directory.GetCurrentDirectory(), "Skills", "terkirdo-computer-use", "SKILL.md"),
                Path.Combine(Directory.GetCurrentDirectory(), "skills", "terkirdo-computer-use", "SKILL.md"),
                Path.Combine(Directory.GetCurrentDirectory(), "src", "TerkirdoComputer", "Skills", "terkirdo-computer-use", "SKILL.md"),
                Path.Combine(Directory.GetCurrentDirectory(), "src", "TerkirdoComputer", "skills", "terkirdo-computer-use", "SKILL.md"),
                Path.Combine(AppContext.BaseDirectory, "Skills", "terkirdo-computer-use", "SKILL.md"),
                Path.Combine(AppContext.BaseDirectory, "skills", "terkirdo-computer-use", "SKILL.md"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "Skills", "terkirdo-computer-use", "SKILL.md"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Skills", "terkirdo-computer-use", "SKILL.md"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Skills", "terkirdo-computer-use", "SKILL.md")
            ];

            string? localSkill = candidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrEmpty(localSkill) && File.Exists(localSkill))
            {
                content = File.ReadAllText(localSkill);
            }
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            content = """
---
name: terkirdo-computer-use
description: Control the Windows desktop (apps, UI automation tree, clicks, text typing, keystrokes, screenshots, scrolling, dragging) via TerkirdoComputer local REST API daemon.
---

# TerkirdoComputer Desktop Automation Skill
See http://127.0.0.1:5005/swagger for full interactive documentation.
Read token from ~/.terkirdo/serve.token and send Authorization: Bearer <token>.
""";
        }

        if (!string.IsNullOrWhiteSpace(customPath))
        {
            string customTargetFile;
            if (customPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                string? parent = Path.GetDirectoryName(customPath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                customTargetFile = customPath;
            }
            else
            {
                Directory.CreateDirectory(customPath);
                customTargetFile = Path.Combine(customPath, "SKILL.md");
            }

            File.WriteAllText(customTargetFile, content);
            Console.WriteLine($"Successfully installed TerkirdoComputer skill to: {customTargetFile}");
            return 0;
        }

        string defaultDir = Path.Combine(home, ".agents", "skills", "terkirdo-computer-use");
        Directory.CreateDirectory(defaultDir);
        string targetFile = Path.Combine(defaultDir, "SKILL.md");
        File.WriteAllText(targetFile, content);
        Console.WriteLine($"Successfully installed TerkirdoComputer skill to: {targetFile}");

        if (Directory.Exists(Path.Combine(home, ".gemini")) || Directory.Exists(Path.Combine(home, ".gemini", "config")))
        {
            string geminiSkills = Path.Combine(home, ".gemini", "config", "skills", "terkirdo-computer-use");
            Directory.CreateDirectory(geminiSkills);
            string geminiSkillFile = Path.Combine(geminiSkills, "SKILL.md");
            File.WriteAllText(geminiSkillFile, content);
            Console.WriteLine($"Also deployed skill to Antigravity/Gemini config skills at: {geminiSkillFile}");
        }

        if (Directory.Exists(Path.Combine(home, ".codex")))
        {
            string codexSkills = Path.Combine(home, ".codex", "skills", "terkirdo-computer-use");
            Directory.CreateDirectory(codexSkills);
            string codexSkillFile = Path.Combine(codexSkills, "SKILL.md");
            File.WriteAllText(codexSkillFile, content);
            Console.WriteLine($"Also deployed skill to Codex skills at: {codexSkillFile}");
        }

        return 0;
    }
}

