// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GUI;

/// <summary>
/// Builds human-readable log filenames while retaining an unknown game's
/// title ID, so logs can still be associated with the correct executable.
/// </summary>
internal static class SessionLogFileName
{
    private static readonly IReadOnlyDictionary<string, string> FriendlyGameNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PPSA04263"] = "GTA5",
            ["PPSA21567"] = "AstroBot",
        };

    internal static string Build(string? titleId, DateTime timestamp) =>
        Build(titleId, CustomVersionLabel.Value, timestamp);

    internal static string Build(string? titleId, string? customVersion, DateTime timestamp)
    {
        var normalizedTitleId = string.IsNullOrWhiteSpace(titleId)
            ? "UNKNOWN"
            : titleId.Trim();
        var gameName = FriendlyGameNames.TryGetValue(normalizedTitleId, out var friendlyName)
            ? friendlyName
            : normalizedTitleId;

        gameName = SanitizeComponent(gameName, "UNKNOWN");
        var version = SanitizeComponent(customVersion, string.Empty);
        var versionPart = string.IsNullOrEmpty(version) ? string.Empty : $"-{version}";

        return $"{gameName}{versionPart}-{timestamp:yyyy-MM-dd_HH-mm}.log";
    }

    private static string SanitizeComponent(string? value, string fallback)
    {
        var result = value?.Trim() ?? string.Empty;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }
}
