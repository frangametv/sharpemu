// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GUI;

internal static class CustomVersionLabel
{
    private const string ResourceName = "SharpEmu.GUI.CUSTOM_VERSION.txt";

    internal static string Value { get; } = Load();

    private static string Load()
    {
        using var stream = typeof(CustomVersionLabel).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}