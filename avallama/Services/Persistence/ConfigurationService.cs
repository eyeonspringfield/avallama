// Copyright (c) Márk Csörgő and Martin Bartos
// Licensed under the MIT License. See LICENSE file for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using avallama.Constants.Keys;
using avallama.Serialization;
using Avalonia.Styling;

namespace avallama.Services.Persistence;

public interface IConfigurationService
{
    string ReadSetting(string key);
    void SaveSetting(string key, string value);
}

public class ConfigurationService : IConfigurationService
{
    private readonly string _configPath;
    private readonly Dictionary<string, string> _settings;
    private readonly Lock _lock = new();

    public ConfigurationService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appDir = Path.Combine(appData, "avallama");
        if (!Directory.Exists(appDir))
        {
            Directory.CreateDirectory(appDir);
        }

        _configPath = Path.Combine(appDir, "config.json");

        if (File.Exists(_configPath))
        {
            var json = File.ReadAllText(_configPath);
            _settings = JsonSerializer.Deserialize(json, AvallamaJsonSerializerContext.Default.StringDictionary) ??
                        new Dictionary<string, string>();
        }
        else
        {
            _settings = new Dictionary<string, string>();
        }
    }

    public string ReadSetting(string key)
    {
        lock (_lock)
        {
            return _settings.TryGetValue(key, out var value) ? value : string.Empty;
        }
    }

    public void SaveSetting(string key, string value)
    {
        lock (_lock)
        {
            _settings[key] = value;
            var json = JsonSerializer.Serialize(
                _settings,
                AvallamaIndentedJsonSerializerContext.Default.StringDictionary);
            File.WriteAllText(_configPath, json);

            if (Avalonia.Application.Current is not App app) return;
            if (key == ConfigurationKey.ColorScheme)
            {
                app.RequestedThemeVariant = value switch
                {
                    "light" => ThemeVariant.Light,
                    "dark" => ThemeVariant.Dark,
                    _ => ThemeVariant.Default
                };
            }
        }
    }
}
