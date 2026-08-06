// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.Core.Runtime;

/// <summary>
/// Describes a user-supplied, extracted system-software filesystem. SharpEmu
/// never owns this tree: every guest mount produced from it is read-only.
/// </summary>
public sealed class SystemSoftwareLayout
{
    private const uint ElfMagic = 0x7F454C46;
    private const uint Ps4SelfMagic = 0x4F153D1D;
    private const uint Ps5SelfMagic = 0x5414F5EE;
    private const int SelfHeaderSize = 32;
    private const int SelfSegmentSize = 32;
    private static readonly byte[] Ps4SelfIdentifier = [
        0x4F, 0x15, 0x3D, 0x1D, 0x00, 0x01, 0x01, 0x12, 0x01, 0x01, 0x00, 0x00,
    ];
    private static readonly byte[] Ps5SelfIdentifier = [
        0x54, 0x14, 0xF5, 0xEE, 0x10, 0x01, 0x01, 0x32, 0x01, 0x03, 0x00, 0x10,
    ];

    private SystemSoftwareLayout(
        string rootPath,
        string entryPath,
        string entryGuestPath,
        EntryInspection entryInspection,
        IReadOnlyList<SystemSoftwareMount> mounts)
    {
        RootPath = rootPath;
        EntryPath = entryPath;
        EntryGuestPath = entryGuestPath;
        EntryImageSummary = entryInspection.Summary;
        EntryCompatibilityError = entryInspection.CompatibilityError;
        Mounts = mounts;
    }

    public string RootPath { get; }

    public string EntryPath { get; }

    public string EntryGuestPath { get; }

    public string EntryImageSummary { get; }

    public string? EntryCompatibilityError { get; }

    public IReadOnlyList<SystemSoftwareMount> Mounts { get; }

    public static SystemSoftwareLayout Create(string? systemRoot, string entryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPath);

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(systemRoot));
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException(
                $"The extracted system-software root was not found: {normalizedRoot}");
        }

        var normalizedEntry = Path.GetFullPath(entryPath);
        if (!File.Exists(normalizedEntry))
        {
            throw new FileNotFoundException("The System UI entry executable was not found.", normalizedEntry);
        }

        var relativeEntry = Path.GetRelativePath(normalizedRoot, normalizedEntry);
        if (IsOutsideRoot(relativeEntry))
        {
            throw new ArgumentException(
                $"The System UI entry must be inside the extracted system-software root. " +
                $"Root='{normalizedRoot}', entry='{normalizedEntry}'.",
                nameof(entryPath));
        }

        if (string.IsNullOrWhiteSpace(Path.GetDirectoryName(relativeEntry)))
        {
            throw new ArgumentException(
                "The System UI entry must be inside one of the root's top-level directories " +
                "so its guest path is backed by a read-only mount.",
                nameof(entryPath));
        }

        var mounts = Directory
            .EnumerateDirectories(normalizedRoot)
            .Select(path => new SystemSoftwareMount(
                GuestPath: "/" + Path.GetFileName(path),
                HostPath: Path.GetFullPath(path),
                ReadOnly: true))
            .OrderBy(mount => mount.GuestPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (mounts.Length == 0)
        {
            throw new InvalidDataException(
                "The selected system-software root has no top-level directories to mount. " +
                "Select the extracted filesystem root, not the directory containing only the shell executable.");
        }

        var entryGuestPath = "/" + relativeEntry
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        var entryInspection = InspectEntry(normalizedEntry);

        return new SystemSoftwareLayout(
            normalizedRoot,
            normalizedEntry,
            entryGuestPath,
            entryInspection,
            mounts);
    }

    public void EnsureEntryIsLoadable()
    {
        if (EntryCompatibilityError is not null)
        {
            throw new InvalidDataException(EntryCompatibilityError);
        }
    }

    public string BuildDiagnosticSummary()
    {
        var entrySize = new FileInfo(EntryPath).Length;
        var builder = new StringBuilder(512);
        builder.AppendLine("System UI preflight:");
        builder.AppendLine($"- Root: {RootPath}");
        builder.AppendLine($"- Entry: {EntryPath}");
        builder.AppendLine($"- Guest entry: {EntryGuestPath}");
        builder.AppendLine($"- Entry size: {entrySize} bytes");
        builder.AppendLine($"- Entry image: {EntryImageSummary}");
        builder.AppendLine($"- Read-only mounts: {Mounts.Count}");
        foreach (var mount in Mounts)
        {
            builder.AppendLine($"  {mount.GuestPath} -> {mount.HostPath} [read-only]");
        }

        builder.Append(EntryCompatibilityError is null
            ? "- Layout and entry image are loadable; emulator compatibility is not implied."
            : "- Layout valid; entry image is not currently loadable.");
        return builder.ToString();
    }

    private static EntryInspection InspectEntry(string entryPath)
    {
        using var stream = File.OpenRead(entryPath);
        if (stream.Length < sizeof(uint))
        {
            return new EntryInspection(
                "unknown",
                "The System UI entry is too small to contain an ELF or SELF header.");
        }

        Span<byte> magicBytes = stackalloc byte[sizeof(uint)];
        stream.ReadExactly(magicBytes);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(magicBytes);
        if (magic == ElfMagic)
        {
            return new EntryInspection("decrypted ELF", null);
        }

        if (magic is not Ps4SelfMagic and not Ps5SelfMagic)
        {
            return new EntryInspection(
                "unknown",
                "The System UI entry is neither a decrypted ELF nor a recognized PS4/PS5 SELF image.");
        }

        var platform = magic == Ps5SelfMagic ? "PS5" : "PS4";
        if (stream.Length < SelfHeaderSize)
        {
            return new EntryInspection(
                $"{platform} SELF (truncated header)",
                $"The selected {platform} SELF header is truncated.");
        }

        Span<byte> selfHeader = stackalloc byte[SelfHeaderSize];
        stream.Position = 0;
        stream.ReadExactly(selfHeader);
        var expectedIdentifier = magic == Ps5SelfMagic ? Ps5SelfIdentifier : Ps4SelfIdentifier;
        var expectedUnknown = magic == Ps5SelfMagic ? (ushort)0x52 : (ushort)0x22;
        var unknown = BinaryPrimitives.ReadUInt16LittleEndian(selfHeader[26..28]);
        if (!selfHeader[..12].SequenceEqual(expectedIdentifier) || unknown != expectedUnknown)
        {
            return new EntryInspection(
                $"{platform} SELF (unrecognized header layout)",
                $"The selected {platform} SELF uses a header layout SharpEmu does not recognize.");
        }

        var segmentCount = BinaryPrimitives.ReadUInt16LittleEndian(selfHeader[24..26]);
        var segmentTableLength = checked(segmentCount * SelfSegmentSize);
        var elfOffset = checked(SelfHeaderSize + segmentTableLength);
        if (stream.Length < elfOffset + sizeof(uint))
        {
            return new EntryInspection(
                $"{platform} SELF ({segmentCount} segments, truncated)",
                $"The selected {platform} SELF segment table or embedded ELF header is truncated.");
        }

        var segmentTable = GC.AllocateUninitializedArray<byte>(segmentTableLength);
        stream.Position = SelfHeaderSize;
        stream.ReadExactly(segmentTable);
        var encryptedSegmentCount = 0;
        var compressedSegmentCount = 0;
        for (var offset = 0; offset < segmentTable.Length; offset += SelfSegmentSize)
        {
            var type = BinaryPrimitives.ReadUInt64LittleEndian(segmentTable.AsSpan(offset, sizeof(ulong)));
            encryptedSegmentCount += (type & 0x2) != 0 ? 1 : 0;
            compressedSegmentCount += (type & 0x8) != 0 ? 1 : 0;
        }

        stream.Position = elfOffset;
        stream.ReadExactly(magicBytes);
        var summary = $"{platform} SELF ({segmentCount} segments, " +
                      $"{encryptedSegmentCount} encrypted, {compressedSegmentCount} compressed)";
        if (BinaryPrimitives.ReadUInt32BigEndian(magicBytes) != ElfMagic)
        {
            return new EntryInspection(
                summary,
                $"The selected {platform} SELF does not contain a valid embedded ELF header.");
        }

        if (encryptedSegmentCount > 0)
        {
            return new EntryInspection(
                summary,
                $"The selected {platform} SELF contains {encryptedSegmentCount} encrypted segment(s). " +
                "Decrypting the outer PUP does not decrypt its SELF executables; supply a lawfully obtained " +
                "decrypted ELF/FSELF dump of the shell.");
        }

        return new EntryInspection(summary, null);
    }

    private static bool IsOutsideRoot(string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || string.Equals(relativePath, "..", StringComparison.Ordinal))
        {
            return true;
        }

        return relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
               relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private readonly record struct EntryInspection(string Summary, string? CompatibilityError);
}

public readonly record struct SystemSoftwareMount(
    string GuestPath,
    string HostPath,
    bool ReadOnly);
