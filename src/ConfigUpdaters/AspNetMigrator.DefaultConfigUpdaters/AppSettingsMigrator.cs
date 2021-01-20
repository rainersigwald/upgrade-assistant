﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.XPath;
using AspNetMigrator.ConfigUpdater;
using Microsoft.Extensions.Logging;

namespace AspNetMigrator.DefaultConfigUpdaters
{
    public class AppSettingsMigrator : IConfigUpdater
    {
        private const string AppSettingsPath = "/configuration/appSettings";
        private const string AddSettingElementName = "add";
        private const string KeyAttributeName = "key";
        private const string ValueAttributeName = "value";
        private const string AppSettingsJsonFileName = "appsettings.json";

        private static readonly Regex AppSettingsFileRegex = new("^appsettings(\\..+)?\\.json$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly ILogger<AppSettingsMigrator> _logger;
        private readonly Dictionary<string, string> _appSettingsToMigrate;

        public string Title => "Migrate appSettings";

        public string Description => "Migrate app settings from app.config and web.config files to appsettings.json";

        public BuildBreakRisk Risk => BuildBreakRisk.Low;

        public AppSettingsMigrator(ILogger<AppSettingsMigrator> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appSettingsToMigrate = new Dictionary<string, string>();
        }

        public async Task<bool> ApplyAsync(IMigrationContext context, ImmutableArray<ConfigFile> configFiles, CancellationToken token)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var project = context.Project.Required();

            // Determine where appsettings.json should live
            var appSettingsPath = Path.Combine(project.Directory ?? string.Empty, AppSettingsJsonFileName);

            // Parse existing appsettings.json properties, if any
            var existingJson = "{}";
            if (File.Exists(appSettingsPath))
            {
                // Read all text instead of keeping the stream open so that we can
                // re-open the config file later in this method as writeable
                existingJson = await File.ReadAllTextAsync(appSettingsPath, token).ConfigureAwait(false);
            }

            using var json = JsonDocument.Parse(existingJson, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var existingProperties = json.RootElement.EnumerateObject();

            // Write an updated appsettings.json file including the previous properties and new ones for the new app settings properties
            using var fs = new FileStream(appSettingsPath, FileMode.Create, FileAccess.Write);
            using var jsonWriter = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
            {
                jsonWriter.WriteStartObject();
                foreach (var property in existingProperties)
                {
                    property.WriteTo(jsonWriter);
                }

                foreach (var setting in _appSettingsToMigrate)
                {
                    if (bool.TryParse(setting.Value, out var boolValue))
                    {
                        jsonWriter.WriteBoolean(setting.Key, boolValue);
                    }
                    else if (long.TryParse(setting.Value, out var longValue))
                    {
                        jsonWriter.WriteNumber(setting.Key, longValue);
                    }
                    else if (double.TryParse(setting.Value, out var doubleValue))
                    {
                        jsonWriter.WriteNumber(setting.Key, doubleValue);
                    }
                    else
                    {
                        jsonWriter.WriteString(setting.Key, setting.Value);
                    }
                }

                jsonWriter.WriteEndObject();
            }

            // Make sure the project is reloaded in case the appsettings.json file was added in this apply step
            await context.ReloadWorkspaceAsync(token).ConfigureAwait(false);

            // Confirm that the appsettings.json file is included in the project. In rare cases (auto-include disabled),
            // it may be necessary to add it explicitly
            var file = project.GetFile();

            if (!file.ContainsItem(appSettingsPath, ProjectItemType.Content, token))
            {
                file.AddItem(ProjectItemType.Content.Name, appSettingsPath);
                await file.SaveAsync(token).ConfigureAwait(false);
            }

            return true;
        }

        public Task<bool> IsApplicableAsync(IMigrationContext context, ImmutableArray<ConfigFile> configFiles, CancellationToken token)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            // Find appSettings elements in the config files
            var appSettings = new Dictionary<string, string>();
            foreach (var configFile in configFiles)
            {
                var appSettingsElement = configFile.Contents.XPathSelectElement(AppSettingsPath);
                if (appSettingsElement is not null)
                {
                    foreach (var setting in appSettingsElement.Elements(AddSettingElementName))
                    {
                        if (setting is not null)
                        {
                            var key = setting.Attribute(KeyAttributeName);
                            var value = setting.Attribute(ValueAttributeName);
                            if (key is not null && value is not null)
                            {
                                _logger.LogDebug("Found app setting {AppSettingName} in {ConfigFilePath}", key.Value, configFile.Path);
                                appSettings[key.Value] = value.Value;
                            }
                        }
                    }
                }
            }

            var project = context.Project.Required();

            var jsonConfigFiles = project.FindFiles(ProjectItemType.Content, AppSettingsFileRegex)
                .Select(f => new AppSettingsFile(f))
                .ToList();

            // Check for existing appSettings.json files for app settings
            foreach (var setting in appSettings)
            {
                if (!jsonConfigFiles.Any(s => !string.IsNullOrEmpty(s.Configuration[setting.Key])))
                {
                    _appSettingsToMigrate.Add(setting.Key, setting.Value);
                }
                else
                {
                    _logger.LogDebug("Existing app settings already include setting {SettingName}", setting.Key);
                }
            }

            _logger.LogInformation("Found {AppSettingCount} app settings for migration: {AppSettingNames}", _appSettingsToMigrate.Count, string.Join(", ", _appSettingsToMigrate.Keys));

            var result = _appSettingsToMigrate.Count > 0;

            return Task.FromResult(result);
        }
    }
}