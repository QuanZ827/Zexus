using System;
using System.IO;
using System.Text.Json;

namespace Zexus.Services
{
    public class AppConfig
    {
        // API Key must be configured by user on first launch via the Settings dialog
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "claude-sonnet-4-20250514";
        public int MaxTokens { get; set; } = 16384;
        public bool EnableStreaming { get; set; } = true;
        public string Provider { get; set; } = "Anthropic";
    }

    public static class ConfigManager
    {
        private static AppConfig _config;
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Zexus", "config.json");

        public static AppConfig Config
        {
            get
            {
                if (_config == null) LoadConfig();
                return _config;
            }
        }

        private static void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    _config = JsonSerializer.Deserialize<AppConfig>(json);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Failed to load config from {ConfigPath}: {ex.Message}");
            }

            if (_config == null)
            {
                _config = new AppConfig();
            }

            // Fallback: if no API key in user config, try team_config.json next to the DLL
            if (string.IsNullOrEmpty(_config.ApiKey) || !LlmProviderInfo.ValidateApiKey(GetProvider(), _config.ApiKey))
            {
                TryLoadTeamConfig();
            }
        }

        /// <summary>
        /// Fallback: read API key from team_config.json deployed alongside the plugin DLL.
        /// This allows shared config distribution via the installer so users don't
        /// need to enter the key manually. User's personal config always takes priority.
        /// </summary>
        private static void TryLoadTeamConfig()
        {
            try
            {
                var dllDir = Path.GetDirectoryName(typeof(ConfigManager).Assembly.Location);
                if (string.IsNullOrEmpty(dllDir)) return;

                var teamConfigPath = Path.Combine(dllDir, "team_config.json");
                if (!File.Exists(teamConfigPath)) return;

                var json = File.ReadAllText(teamConfigPath);
                var teamConfig = JsonSerializer.Deserialize<AppConfig>(json);

                if (teamConfig != null && !string.IsNullOrEmpty(teamConfig.ApiKey)
                    && LlmProviderInfo.ValidateApiKey(GetProvider(), teamConfig.ApiKey))
                {
                    _config.ApiKey = teamConfig.ApiKey;
                    if (!string.IsNullOrEmpty(teamConfig.Provider))
                        _config.Provider = teamConfig.Provider;
                    System.Diagnostics.Debug.WriteLine("[Zexus] Loaded API key from team_config.json");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Failed to load team config: {ex.Message}");
            }
        }

        private static void SaveConfig()
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Failed to save config to {ConfigPath}: {ex.Message}");
            }
        }

        public static bool IsConfigured()
        {
            return !string.IsNullOrEmpty(Config.ApiKey)
                && LlmProviderInfo.ValidateApiKey(GetProvider(), Config.ApiKey);
        }

        public static string GetApiKey() => Config.ApiKey;
        public static string GetModel() => Config.Model;

        public static LlmProvider GetProvider()
        {
            return LlmProviderInfo.Parse(Config.Provider);
        }

        public static void SetApiKey(string apiKey)
        {
            Config.ApiKey = apiKey;
            SaveConfig();
        }

        public static void SetModel(string model)
        {
            Config.Model = model;
            SaveConfig();
        }

        public static void SetProvider(LlmProvider provider)
        {
            Config.Provider = provider.ToString();
            // Update model to the new provider's default if current model belongs to another provider
            Config.Model = LlmProviderInfo.GetDefaultModel(provider);
            SaveConfig();
        }
    }
}
