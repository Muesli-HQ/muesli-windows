using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Muesli.Windows.Tests;

/// <summary>
/// The single source of truth for test-time repository discovery and bounded source enumeration.
/// <para>
/// Root discovery uses durable active markers (a <c>.git</c> entry, or <c>global.json</c> together
/// with the active solution/WinUI project) rather than the retired
/// <c>windows-native/Muesli.Windows/Muesli.Windows.csproj</c> path, so it works from the repository
/// root, <c>bin</c> output, Visual Studio, <c>dotnet test</c>, CI and a relocated checkout.
/// </para>
/// <para>
/// Enumeration never descends into build output, never follows reparse points, and cannot loop.
/// </para>
/// </summary>
public static class TestRepositoryLayout
{
    private static readonly string[] ProjectMarkers =
    {
        System.IO.Path.Combine("windows-native", "Muesli.Windows.sln"),
        System.IO.Path.Combine("windows-native", "Muesli.Windows.WinUI", "Muesli.Windows.WinUI.csproj"),
    };

    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        ".build",
        ".swiftpm",
        ".git",
        ".vs",
        "artifacts",
        "node_modules",
        "packages",
        "TestResults",
        "runtimes",
        "AppX",
    };

    public static string Root { get; } = Discover();

    public static string Combine(params string[] segments) =>
        System.IO.Path.Combine(new[] { Root }.Concat(segments).ToArray());

    /// <summary>
    /// Enumerates source files under a repository-relative root, skipping generated output and
    /// reparse points. Deterministic and safe in a dirty development tree.
    /// </summary>
    public static IEnumerable<string> EnumerateSourceFiles(string relativeRoot, string searchPattern = "*.cs")
    {
        var root = System.IO.Path.Combine(Root, relativeRoot);
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(current, searchPattern))
            {
                yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(current))
            {
                if (ExcludedDirectoryNames.Contains(System.IO.Path.GetFileName(child)))
                {
                    continue;
                }

                try
                {
                    if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }
                }
                catch (IOException)
                {
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    private static string Discover()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var git = System.IO.Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return directory.FullName;
            }

            if (File.Exists(System.IO.Path.Combine(directory.FullName, "global.json")) &&
                ProjectMarkers.Any(marker => File.Exists(System.IO.Path.Combine(directory.FullName, marker))))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Muesli repository root from " + AppContext.BaseDirectory);
    }
}
