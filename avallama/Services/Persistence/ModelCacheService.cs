// Copyright (c) Márk Csörgő and Martin Bartos
// Licensed under the MIT License. See LICENSE file for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using avallama.Constants.Keys;
using avallama.Models.Ollama;
using avallama.Serialization;
using avallama.Utilities;
using Microsoft.Data.Sqlite;

namespace avallama.Services.Persistence;

public interface IModelCacheService
{
    Task CacheModelFamilyAsync(IList<OllamaModelFamily> modelFamilies);
    Task CacheModelsAsync(IList<OllamaModel> models);
    Task UpdateModelAsync(OllamaModel model);
    Task InsertModelAsync(OllamaModel model);
    Task<bool> ContainsModelAsync(OllamaModel model);
    Task<IList<OllamaModel>> GetDownloadedModelsAsync();
    Task<IList<OllamaModel>> GetCachedModelsAsync();
    Task<IList<OllamaModelFamily>> GetCachedModelFamiliesAsync();
}

public class ModelCacheService : IModelCacheService
{
    private readonly SqliteConnection _connection;

    public ModelCacheService(SqliteConnection? testConnection = null)
    {
        if (testConnection is not null)
        {
            _connection = testConnection;
            _connection.Open();
            return;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appDir = Path.Combine(appData, "avallama");
        if (!Directory.Exists(appDir))
            Directory.CreateDirectory(appDir);

        var dbPath = Path.Combine(appDir, "avallama.db");
        var connectionString = $"Data Source={dbPath}";
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
    }

    public async Task CacheModelFamilyAsync(IList<OllamaModelFamily> modelFamilies)
    {
        var now = DateTime.Now;

        using (DatabaseLock.Instance.AcquireWriteLock())
        {
            await using var transaction = _connection.BeginTransaction();

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT name, description, pull_count, labels, tag_count, last_updated FROM model_families";
            await using var reader = await cmd.ExecuteReaderAsync();
            var existingModelFamilies = new Dictionary<string, OllamaModelFamily>();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                var description = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var pullCount = reader.GetInt32(2);
                var labels = reader.IsDBNull(3)
                    ? []
                    : JsonSerializer.Deserialize(
                        reader.GetString(3),
                        AvallamaJsonSerializerContext.Default.StringList) ?? [];
                var tagCount = reader.GetInt32(4);
                var lastUpdated = reader.IsDBNull(5) ? DateTime.MinValue : reader.GetDateTime(5);

                var modelFamily = new OllamaModelFamily
                {
                    Name = name,
                    Description = description,
                    PullCount = pullCount,
                    Labels = labels,
                    TagCount = tagCount,
                    LastUpdated = lastUpdated
                };

                existingModelFamilies[name] = modelFamily;
            }

            reader.Close();

            var newFamilies = modelFamilies.ToDictionary(f => f.Name);

            var toInsert = modelFamilies.Where(f => !existingModelFamilies.ContainsKey(f.Name));
            var toDelete = existingModelFamilies.Values.Where(f => !newFamilies.ContainsKey(f.Name));
            var toUpdate = modelFamilies
                .Where(f => existingModelFamilies.ContainsKey(f.Name))
                .Where(f => HasModelFamilyChanged(f, existingModelFamilies[f.Name]));

            var insertList = toInsert.ToList();
            const int batchSize = 100;

            for (var offset = 0; offset < insertList.Count; offset += batchSize)
            {
                var batch = insertList.Skip(offset).Take(batchSize);
                foreach (var family in batch)
                {
                    await using var insertCmd = _connection.CreateCommand();
                    insertCmd.CommandText = """
                                            INSERT INTO model_families
                                                (name, description, pull_count, labels,
                                                 tag_count, last_updated, cached_at)
                                            VALUES
                                                (@name, @description, @pullCount, @labels,
                                                 @tagCount, @lastUpdated, @cachedAt)
                                            """;

                    insertCmd.Parameters.AddWithValue("@name", family.Name);
                    insertCmd.Parameters.AddWithValue("@description", family.Description);
                    insertCmd.Parameters.AddWithValue("@pullCount", family.PullCount);
                    insertCmd.Parameters.AddWithValue("@labels",
                        JsonSerializer.Serialize(
                            family.Labels.ToList(),
                            AvallamaJsonSerializerContext.Default.StringList));
                    insertCmd.Parameters.AddWithValue("@tagCount", family.TagCount);
                    insertCmd.Parameters.AddWithValue("@lastUpdated", family.LastUpdated);
                    insertCmd.Parameters.AddWithValue("@cachedAt", now);

                    await insertCmd.ExecuteNonQueryAsync();
                }
            }

            var updateList = toUpdate.ToList();
            for (var offset = 0; offset < updateList.Count; offset += batchSize)
            {
                var batch = updateList.Skip(offset).Take(batchSize);
                foreach (var family in batch)
                {
                    await using var updateCmd = _connection.CreateCommand();
                    updateCmd.CommandText = """
                                            UPDATE model_families
                                            SET description = @description,
                                                pull_count = @pullCount,
                                                labels = @labels,
                                                tag_count = @tagCount,
                                                last_updated = @lastUpdated,
                                                cached_at = @cachedAt
                                            WHERE name = @name
                                            """;

                    updateCmd.Parameters.AddWithValue("@name", family.Name);
                    updateCmd.Parameters.AddWithValue("@description", family.Description);
                    updateCmd.Parameters.AddWithValue("@pullCount", family.PullCount);
                    updateCmd.Parameters.AddWithValue("@labels",
                        JsonSerializer.Serialize(
                            family.Labels.ToList(),
                            AvallamaJsonSerializerContext.Default.StringList));
                    updateCmd.Parameters.AddWithValue("@tagCount", family.TagCount);
                    updateCmd.Parameters.AddWithValue("@lastUpdated", family.LastUpdated);
                    updateCmd.Parameters.AddWithValue("@cachedAt", now);

                    await updateCmd.ExecuteNonQueryAsync();
                }
            }

            var deleteList = toDelete.ToList();
            if (deleteList.Count != 0)
            {
                await using var deleteCmd = _connection.CreateCommand();
                var nameParams = string.Join(", ", deleteList.Select((_, i) => $"@name{i}"));
                deleteCmd.CommandText = $"DELETE FROM model_families WHERE name IN ({nameParams})";

                for (var i = 0; i < deleteList.Count; i++)
                {
                    deleteCmd.Parameters.AddWithValue($"@name{i}", deleteList[i].Name);
                }

                await deleteCmd.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }
    }

    private static bool HasModelFamilyChanged(OllamaModelFamily newFamily, OllamaModelFamily existingFamily)
    {
        return newFamily.Description != existingFamily.Description ||
               newFamily.PullCount != existingFamily.PullCount ||
               newFamily.TagCount != existingFamily.TagCount ||
               newFamily.LastUpdated != existingFamily.LastUpdated ||
               !newFamily.Labels.SequenceEqual(existingFamily.Labels);
    }

    public async Task<IList<OllamaModelFamily>> GetCachedModelFamiliesAsync()
    {
        var modelFamilies = new List<OllamaModelFamily>();
        using (DatabaseLock.Instance.AcquireReadLock())
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT name, description, pull_count, labels, tag_count, last_updated FROM model_families ORDER BY pull_count DESC";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                var description = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var pullCount = reader.GetInt32(2);
                var labels = reader.IsDBNull(3)
                    ? []
                    : JsonSerializer.Deserialize(
                        reader.GetString(3),
                        AvallamaJsonSerializerContext.Default.StringList) ?? [];
                var tagCount = reader.GetInt32(4);
                var lastUpdated = reader.IsDBNull(5) ? DateTime.MinValue : reader.GetDateTime(5);

                var modelFamily = new OllamaModelFamily
                {
                    Name = name,
                    Description = description,
                    PullCount = pullCount,
                    Labels = labels,
                    TagCount = tagCount,
                    LastUpdated = lastUpdated
                };

                modelFamilies.Add(modelFamily);
            }
        }

        return modelFamilies;
    }

    public async Task CacheModelsAsync(IList<OllamaModel> models)
    {
        var now = DateTime.Now;

        using (DatabaseLock.Instance.AcquireWriteLock())
        {
            await using var transaction = _connection.BeginTransaction();

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                                      SELECT name, family_name, parameters, size, format, quantization,
                                             architecture, block_count, context_length, embedding_length,
                                             additional_info, is_downloaded
                                      FROM ollama_models
                              """;

            await using var reader = await cmd.ExecuteReaderAsync();
            var existingModels = new Dictionary<string, OllamaModel>();

            while (await reader.ReadAsync())
            {
                var info = new Dictionary<string, string>();

                if (!reader.IsDBNull(4)) info[ModelInfoKey.Format] = reader.GetString(4);
                if (!reader.IsDBNull(5)) info[ModelInfoKey.QuantizationLevel] = reader.GetString(5);
                if (!reader.IsDBNull(6)) info[ModelInfoKey.Architecture] = reader.GetString(6);
                if (!reader.IsDBNull(7)) info[ModelInfoKey.BlockCount] = reader.GetInt32(7).ToString();
                if (!reader.IsDBNull(8)) info[ModelInfoKey.ContextLength] = reader.GetInt32(8).ToString();
                if (!reader.IsDBNull(9)) info[ModelInfoKey.EmbeddingLength] = reader.GetInt32(9).ToString();

                if (!reader.IsDBNull(10))
                {
                    var additionalInfo = JsonSerializer.Deserialize(
                        reader.GetString(10),
                        AvallamaJsonSerializerContext.Default.StringDictionary);
                    if (additionalInfo != null)
                    {
                        foreach (var kvp in additionalInfo)
                        {
                            info[kvp.Key] = kvp.Value;
                        }
                    }
                }

                var model = new OllamaModel
                {
                    Name = reader.GetString(0),
                    Parameters = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    Size = reader.GetInt64(3),
                    Info = info,
                    IsDownloaded = reader.GetInt32(11) != 0
                };

                existingModels[model.Name] = model;
            }

            reader.Close();

            var newModels = models.ToDictionary(m => m.Name);
            var toInsert = models.Where(m => !existingModels.ContainsKey(m.Name));
            var toDelete = existingModels.Values.Where(m => !newModels.ContainsKey(m.Name));
            var toUpdate = models
                .Where(m => existingModels.ContainsKey(m.Name))
                .Where(m => HasModelChanged(m, existingModels[m.Name]));

            var insertList = toInsert.ToList();
            const int batchSize = 100;

            for (var offset = 0; offset < insertList.Count; offset += batchSize)
            {
                var batch = insertList.Skip(offset).Take(batchSize);
                foreach (var model in batch)
                {
                    var (format, quantization, architecture, blockCount, contextLength, embeddingLength,
                            additionalInfo) =
                        ExtractModelInfo(model.Info);

                    await using var insertCmd = _connection.CreateCommand();
                    insertCmd.CommandText = """
                                            INSERT INTO ollama_models
                                                (name, family_name, parameters, size, format,
                                                 quantization, architecture, block_count,
                                                 context_length, embedding_length, additional_info,
                                                 is_downloaded, cached_at)
                                            VALUES
                                                (@name, @familyName, @parameters, @size, @format,
                                                 @quantization, @architecture, @blockCount,
                                                 @contextLength, @embeddingLength, @additionalInfo,
                                                 @isDownloaded, @cachedAt)
                                            """;

                    insertCmd.Parameters.AddWithValue("@name", model.Name);
                    insertCmd.Parameters.AddWithValue("@familyName", ExtractFamilyName(model.Name));
                    insertCmd.Parameters.AddWithValue("@parameters", model.Parameters);
                    insertCmd.Parameters.AddWithValue("@size", model.Size);
                    insertCmd.Parameters.AddWithValue("@format", format ?? (object)DBNull.Value);
                    insertCmd.Parameters.AddWithValue("@quantization", quantization ?? (object)DBNull.Value);
                    insertCmd.Parameters.AddWithValue("@architecture", architecture ?? (object)DBNull.Value);
                    insertCmd.Parameters.AddWithValue("@blockCount", blockCount ?? (object)DBNull.Value);
                    insertCmd.Parameters.AddWithValue("@contextLength", contextLength ?? (object)DBNull.Value);
                    insertCmd.Parameters.AddWithValue("@embeddingLength", embeddingLength ?? (object)DBNull.Value);
                    insertCmd.Parameters.AddWithValue("@additionalInfo", additionalInfo ?? (object)DBNull.Value);
                    insertCmd.Parameters.AddWithValue("@isDownloaded", model.IsDownloaded ? 1 : 0);
                    insertCmd.Parameters.AddWithValue("@cachedAt", now);

                    await insertCmd.ExecuteNonQueryAsync();
                }
            }

            var updateList = toUpdate.ToList();

            for (var offset = 0; offset < updateList.Count; offset += batchSize)
            {
                var batch = updateList.Skip(offset).Take(batchSize);
                foreach (var model in batch)
                {
                    var (format, quantization, architecture, blockCount, contextLength, embeddingLength,
                            additionalInfo) =
                        ExtractModelInfo(model.Info);

                    await using var updateCmd = _connection.CreateCommand();
                    updateCmd.CommandText = """
                                            UPDATE ollama_models
                                            SET family_name = @familyName,
                                                parameters = @parameters,
                                                size = @size,
                                                format = @format,
                                                quantization = @quantization,
                                                architecture = @architecture,
                                                block_count = @blockCount,
                                                context_length = @contextLength,
                                                embedding_length = @embeddingLength,
                                                additional_info = @additionalInfo,
                                                is_downloaded = @isDownloaded,
                                                cached_at = @cachedAt
                                            WHERE name = @name
                                            """;

                    updateCmd.Parameters.AddWithValue("@name", model.Name);
                    updateCmd.Parameters.AddWithValue("@familyName", ExtractFamilyName(model.Name));
                    updateCmd.Parameters.AddWithValue("@parameters", model.Parameters);
                    updateCmd.Parameters.AddWithValue("@size", model.Size);
                    updateCmd.Parameters.AddWithValue("@format", format ?? (object)DBNull.Value);
                    updateCmd.Parameters.AddWithValue("@quantization", quantization ?? (object)DBNull.Value);
                    updateCmd.Parameters.AddWithValue("@architecture", architecture ?? (object)DBNull.Value);
                    updateCmd.Parameters.AddWithValue("@blockCount", blockCount ?? (object)DBNull.Value);
                    updateCmd.Parameters.AddWithValue("@contextLength", contextLength ?? (object)DBNull.Value);
                    updateCmd.Parameters.AddWithValue("@embeddingLength", embeddingLength ?? (object)DBNull.Value);
                    updateCmd.Parameters.AddWithValue("@additionalInfo", additionalInfo ?? (object)DBNull.Value);
                    updateCmd.Parameters.AddWithValue("@isDownloaded", model.IsDownloaded ? 1 : 0);
                    updateCmd.Parameters.AddWithValue("@cachedAt", now);

                    await updateCmd.ExecuteNonQueryAsync();
                }
            }

            var deleteList = toDelete.ToList();
            if (deleteList.Count != 0)
            {
                await using var deleteCmd = _connection.CreateCommand();
                var nameParams = string.Join(", ", deleteList.Select((_, i) => $"@name{i}"));
                deleteCmd.CommandText = $"DELETE FROM ollama_models WHERE name IN ({nameParams})";

                for (var i = 0; i < deleteList.Count; i++)
                {
                    deleteCmd.Parameters.AddWithValue($"@name{i}", deleteList[i].Name);
                }

                await deleteCmd.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }
    }

    private static (string?, string?, string?, int?, int?, int?, string?) ExtractModelInfo(
        IDictionary<string, string>? info)
    {
        if (info == null) return (null, null, null, null, null, null, null);

        var format = info.TryGetValue(ModelInfoKey.Format, out var fmt) ? fmt : null;
        var quantization = info.TryGetValue(ModelInfoKey.QuantizationLevel, out var qntzn)
            ? qntzn
            : null;
        var architecture = info.TryGetValue(ModelInfoKey.Architecture, out var arch)
            ? arch
            : null;
        var blockCount = info.TryGetValue(ModelInfoKey.BlockCount, out var bc) &&
                         int.TryParse(bc, out var bcVal)
            ? bcVal
            : (int?)null;
        var contextLength = info.TryGetValue(ModelInfoKey.ContextLength, out var cl) &&
                            int.TryParse(cl, out var clVal)
            ? clVal
            : (int?)null;
        var embeddingLength = info.TryGetValue(ModelInfoKey.EmbeddingLength, out var el) &&
                              int.TryParse(el, out var elVal)
            ? elVal
            : (int?)null;

        var additionalDetails = info
            .Where(kvp => kvp.Key is not (ModelInfoKey.Format or ModelInfoKey.QuantizationLevel
                or ModelInfoKey.Architecture or ModelInfoKey.BlockCount
                or ModelInfoKey.ContextLength or ModelInfoKey.EmbeddingLength or ModelInfoKey.PullCount
                or ModelInfoKey.LastUpdated))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        var additionalInfo = additionalDetails.Count > 0
            ? JsonSerializer.Serialize(
                additionalDetails,
                AvallamaJsonSerializerContext.Default.StringDictionary)
            : null;

        return (format, quantization, architecture, blockCount, contextLength, embeddingLength, additionalInfo);
    }

    private static string ExtractFamilyName(string modelName)
    {
        var colonIndex = modelName.IndexOf(':');
        return colonIndex > 0 ? modelName[..colonIndex] : modelName;
    }

    private static bool HasModelChanged(OllamaModel newModel, OllamaModel existingModel)
    {
        if (newModel.Parameters != existingModel.Parameters ||
            newModel.Size != existingModel.Size ||
            newModel.IsDownloaded != existingModel.IsDownloaded)
        {
            return true;
        }

        if (newModel.Info.TryGetValue(ModelInfoKey.Format, out var newFormat) !=
            existingModel.Info.TryGetValue(ModelInfoKey.Format, out var oldFormat) ||
            !string.Equals(newFormat, oldFormat, StringComparison.Ordinal))
        {
            return true;
        }

        if (newModel.Info.TryGetValue(ModelInfoKey.QuantizationLevel, out var newQuant) !=
            existingModel.Info.TryGetValue(ModelInfoKey.QuantizationLevel, out var oldQuant) ||
            !string.Equals(newQuant, oldQuant, StringComparison.Ordinal))
        {
            return true;
        }

        if (newModel.Info.Count != existingModel.Info.Count)
            return true;

        if (newModel.Info.Count != existingModel.Info.Count) return true;
        foreach (var kv in newModel.Info)
        {
            if (!existingModel.Info.TryGetValue(kv.Key, out var otherVal) ||
                !string.Equals(kv.Value, otherVal, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public async Task UpdateModelAsync(OllamaModel model)
    {
        var now = DateTime.Now;

        using (DatabaseLock.Instance.AcquireWriteLock())
        {
            await using var transaction = _connection.BeginTransaction();

            await using var cmd = _connection.CreateCommand();

            cmd.CommandText = """
                              UPDATE ollama_models
                              SET parameters = @parameters,
                                  size = @size,
                                  format = @format,
                                  quantization = @quantization,
                                  architecture = @architecture,
                                  block_count = @blockCount,
                                  context_length = @contextLength,
                                  embedding_length = @embeddingLength,
                                  additional_info = @additionalInfo,
                                  is_downloaded = @isDownloaded,
                                  cached_at = @cachedAt
                              WHERE name = @name
                              """;

            var (format, quantization, architecture, blockCount, contextLength, embeddingLength, additionalInfo) =
                ExtractModelInfo(model.Info);

            cmd.Parameters.AddWithValue("@name", model.Name);
            cmd.Parameters.AddWithValue("@parameters", model.Parameters);
            cmd.Parameters.AddWithValue("@size", model.Size);
            cmd.Parameters.AddWithValue("@format", format ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@quantization", quantization ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@architecture", architecture ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@blockCount", blockCount ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@contextLength", contextLength ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@embeddingLength", embeddingLength ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@additionalInfo", additionalInfo ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@isDownloaded", model.IsDownloaded ? 1 : 0);
            cmd.Parameters.AddWithValue("@cachedAt", now);

            await cmd.ExecuteNonQueryAsync();
            transaction.Commit();
        }
    }

    public async Task<IList<OllamaModel>> GetCachedModelsAsync()
    {
        var models = new List<OllamaModel>();

        using (DatabaseLock.Instance.AcquireReadLock())
        {
            // Preload model families into a dictionary for quick lookup
            var familiesByName = await GetModelFamiliesByNameAsync();

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT name, family_name, parameters, size, format, quantization, architecture, " +
                "block_count, context_length, embedding_length, additional_info, is_downloaded " +
                "FROM ollama_models ORDER BY name NULLS LAST";

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var info = new Dictionary<string, string>();

                if (!reader.IsDBNull(4)) info[ModelInfoKey.Format] = reader.GetString(4);
                if (!reader.IsDBNull(5)) info[ModelInfoKey.QuantizationLevel] = reader.GetString(5);
                if (!reader.IsDBNull(6)) info[ModelInfoKey.Architecture] = reader.GetString(6);
                if (!reader.IsDBNull(7)) info[ModelInfoKey.BlockCount] = reader.GetInt32(7).ToString();
                if (!reader.IsDBNull(8)) info[ModelInfoKey.ContextLength] = reader.GetInt32(8).ToString();
                if (!reader.IsDBNull(9)) info[ModelInfoKey.EmbeddingLength] = reader.GetInt32(9).ToString();

                if (!reader.IsDBNull(10))
                {
                    var additionalInfo =
                        JsonSerializer.Deserialize(
                            reader.GetString(10),
                            AvallamaJsonSerializerContext.Default.StringDictionary);
                    if (additionalInfo != null)
                    {
                        foreach (var kvp in additionalInfo)
                        {
                            info[kvp.Key] = kvp.Value;
                        }
                    }
                }

                var name = reader.GetString(0);
                var familyName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

                familiesByName.TryGetValue(familyName, out var family);

                var model = new OllamaModel
                {
                    Name = name,
                    Family = family,
                    Parameters = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    Size = reader.GetInt64(3),
                    Info = info,
                    IsDownloaded = reader.GetInt32(11) != 0
                };

                models.Add(model);
            }
        }

        return models;
    }

    public async Task InsertModelAsync(OllamaModel model)
    {
        var now = DateTime.Now;

        var familyName = ExtractFamilyName(model.Name);

        using (DatabaseLock.Instance.AcquireWriteLock())
        {
            await using var transaction = _connection.BeginTransaction();

            // insert model family (or ignore if it already exits)
            // this is necessary since models can't exist in the db without a family, so lovely
            await using var familyCmd = _connection.CreateCommand();
            familyCmd.CommandText = """
                                    INSERT OR IGNORE INTO model_families
                                        (name, description, pull_count, labels,
                                         tag_count, last_updated, cached_at)
                                    VALUES
                                        (@familyName, @description, @pullCount, @labels,
                                         @tagCount, @lastUpdated, @cachedAt)
                                    """;

            // family data from the model parameter, if it's null we use a default value
            var description = model.Family?.Description ?? string.Empty;
            var pullCount = model.Family?.PullCount ?? 0;
            var labels = model.Family?.Labels ?? new List<string>();
            var tagCount = model.Family?.TagCount ?? 0;
            var lastUpdated = model.Family?.LastUpdated ?? DateTime.MinValue;

            familyCmd.Parameters.AddWithValue("@familyName", familyName);
            familyCmd.Parameters.AddWithValue("@description", description);
            familyCmd.Parameters.AddWithValue("@pullCount", pullCount);
            familyCmd.Parameters.AddWithValue("@labels", JsonSerializer.Serialize(
                labels.ToList(),
                AvallamaJsonSerializerContext.Default.StringList));
            familyCmd.Parameters.AddWithValue("@tagCount", tagCount);
            familyCmd.Parameters.AddWithValue("@lastUpdated", lastUpdated);
            familyCmd.Parameters.AddWithValue("@cachedAt", now);

            await familyCmd.ExecuteNonQueryAsync();

            // insert the actual model
            await using var cmd = _connection.CreateCommand();

            cmd.CommandText = """
                              INSERT INTO ollama_models
                                  (name, family_name, parameters, size, format,
                                   quantization, architecture, block_count,
                                   context_length, embedding_length, additional_info,
                                   is_downloaded, cached_at)
                              VALUES
                                  (@name, @familyName, @parameters, @size, @format,
                                   @quantization, @architecture, @blockCount,
                                   @contextLength, @embeddingLength, @additionalInfo,
                                   @isDownloaded, @cachedAt)
                              """;

            var (format, quantization, architecture, blockCount, contextLength, embeddingLength, additionalInfo) =
                ExtractModelInfo(model.Info);

            cmd.Parameters.AddWithValue("@name", model.Name);
            cmd.Parameters.AddWithValue("@familyName", familyName);
            cmd.Parameters.AddWithValue("@parameters", model.Parameters);
            cmd.Parameters.AddWithValue("@size", model.Size);
            cmd.Parameters.AddWithValue("@format", format ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@quantization", quantization ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@architecture", architecture ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@blockCount", blockCount ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@contextLength", contextLength ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@embeddingLength", embeddingLength ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@additionalInfo", additionalInfo ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@isDownloaded", model.IsDownloaded ? 1 : 0);
            cmd.Parameters.AddWithValue("@cachedAt", now);

            await cmd.ExecuteNonQueryAsync();

            transaction.Commit();
        }
    }

    // lightweight operation to get which models are downloaded according to the cache database, nothing else
    public async Task<IList<OllamaModel>> GetDownloadedModelsAsync()
    {
        var models = new List<OllamaModel>();

        using (DatabaseLock.Instance.AcquireReadLock())
        {
            await using var cmd = _connection.CreateCommand();

            cmd.CommandText = "SELECT name FROM ollama_models WHERE is_downloaded = 1 ORDER BY name";

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);

                var model = new OllamaModel
                {
                    Name = name,
                    IsDownloaded = true
                };

                models.Add(model);
            }
        }

        return models;
    }

    public async Task<bool> ContainsModelAsync(OllamaModel model)
    {
        using (DatabaseLock.Instance.AcquireReadLock())
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM ollama_models WHERE name = @name LIMIT 1";
            cmd.Parameters.AddWithValue("@name", model.Name);

            var result = await cmd.ExecuteScalarAsync();
            return result != null;
        }
    }

    private async Task<Dictionary<string, OllamaModelFamily>> GetModelFamiliesByNameAsync()
    {
        var families = new Dictionary<string, OllamaModelFamily>(StringComparer.Ordinal);

        // Lock held by caller
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT name, description, pull_count, labels, tag_count, last_updated FROM model_families";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            var description = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var pullCount = reader.GetInt32(2);
            var labels = reader.IsDBNull(3)
                ? []
                : JsonSerializer.Deserialize(
                    reader.GetString(3),
                    AvallamaJsonSerializerContext.Default.StringList) ?? [];
            var tagCount = reader.GetInt32(4);
            var lastUpdated = reader.IsDBNull(5) ? DateTime.MinValue : reader.GetDateTime(5);

            families[name] = new OllamaModelFamily
            {
                Name = name,
                Description = description,
                PullCount = pullCount,
                Labels = labels,
                TagCount = tagCount,
                LastUpdated = lastUpdated
            };
        }

        return families;
    }
}
