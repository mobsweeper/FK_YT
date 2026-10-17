using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace FkYouTube.Services;

internal static class ToolchainValidation
{
    internal static string NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("The tool version is missing or invalid.");
        if (value.StartsWith('v')) value = value[1..];
        if (!Regex.IsMatch(value, @"^\d{1,5}(?:\.\d{1,9}){1,3}\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("This stable release version is not supported by this app.");
        return string.Join('.', value.Split('.').Select(part => int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)));
    }
    internal static int CompareVersions(string first, string second)
    {
        var a = NormalizeVersion(first).Split('.').Select(int.Parse).ToArray();
        if (second == "unknown") return 1;
        var b = NormalizeVersion(second).Split('.').Select(int.Parse).ToArray();
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var result = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
            if (result != 0) return result;
        }
        return 0;
    }
    internal static void RequireVersion(string? requirement, string version)
    {
        if (string.IsNullOrWhiteSpace(requirement)) throw new InvalidDataException("The upstream compatibility requirement is missing.");
        foreach (var clause in requirement.Split(','))
        {
            var match = Regex.Match(clause.Trim(), @"^(>=|<=|==|!=|>|<|~=)\s*(\d+(?:\.\d+){0,3})\z", RegexOptions.CultureInvariant);
            if (!match.Success) throw new InvalidDataException("This upstream version requirement needs a newer FK_YT app.");
            var bound = match.Groups[2].Value;
            if (!bound.Contains('.'))
            {
                if (match.Groups[1].Value == "~=") throw new InvalidDataException("The compatible-release requirement is invalid.");
                bound += ".0";
            }
            var compared = CompareVersions(version, bound);
            var accepted = match.Groups[1].Value switch
            {
                ">=" => compared >= 0, "<=" => compared <= 0, "==" => compared == 0,
                "!=" => compared != 0, ">" => compared > 0, "<" => compared < 0,
                "~=" => compared >= 0 && CompatibleUpperBound(version, bound), _ => false
            };
            if (!accepted) throw new InvalidDataException("The selected runtime does not satisfy the upstream compatibility requirement.");
        }
    }
    private static bool CompatibleUpperBound(string version, string bound)
    {
        var parts = NormalizeVersion(bound).Split('.').Select(int.Parse).ToArray();
        var index = parts.Length - 2;
        parts[index] = checked(parts[index] + 1);
        for (var i = index + 1; i < parts.Length; i++) parts[i] = 0;
        return CompareVersions(version, string.Join('.', parts)) < 0;
    }
    /// <summary>Evaluates the supported dependency-marker subset for this app's bundled Windows CPython.</summary>
    internal static bool DependencyMarkerApplies(string requirement, string extra, string pythonVersion)
    {
        if (string.IsNullOrWhiteSpace(requirement) || requirement.Length > 4096 ||
            requirement.Any(c => char.IsControl(c) && c != '\t'))
            throw UnsupportedDependencyMarker();
        var separator = requirement.IndexOf(';');
        var header = (separator < 0 ? requirement : requirement[..separator]).Trim();
        var name = Regex.Match(header, @"^[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?", RegexOptions.CultureInvariant);
        if (!name.Success) throw UnsupportedDependencyMarker();
        var specifiers = header[name.Length..].Trim();
        if (specifiers.Length != 0)
        {
            foreach (var clause in specifiers.Split(','))
            {
                var specifier = Regex.Match(clause.Trim(), @"^(>=|<=|==|!=|>|<|~=)\s*([0-9]{1,5}(?:\.[0-9]{1,9}){0,3})(\.\*)?\z", RegexOptions.CultureInvariant);
                if (!specifier.Success || specifier.Groups[3].Success && specifier.Groups[1].Value is not ("==" or "!=") ||
                    specifier.Groups[1].Value == "~=" && !specifier.Groups[2].Value.Contains('.'))
                    throw UnsupportedDependencyMarker();
            }
        }
        if (separator < 0) return true;
        var parser = new DependencyMarkerParser(requirement[(separator + 1)..], NormalizeExtra(extra), NormalizeVersion(pythonVersion));
        // Parse every branch before evaluating; an inactive extra cannot hide unsupported syntax.
        return parser.Parse()();
    }
    private static InvalidDataException UnsupportedDependencyMarker() =>
        new("This upstream dependency requirement needs a newer FK_YT app.");
    private static string NormalizeExtra(string value)
    {
        if (value is null || value.Length != 0 && !Regex.IsMatch(value, @"^[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?\z", RegexOptions.CultureInvariant))
            throw UnsupportedDependencyMarker();
        return Regex.Replace(value.ToLowerInvariant(), "[-_.]+", "-");
    }
    private sealed class DependencyMarkerParser(string text, string extra, string pythonVersion)
    {
        private int _position, _tokens;
        private string _token = "";
        private MarkerToken _kind;
        internal Func<bool> Parse()
        {
            Next();
            var expression = ParseOr(0);
            if (_kind != MarkerToken.End) throw UnsupportedDependencyMarker();
            return expression;
        }
        private Func<bool> ParseOr(int depth)
        {
            var expression = ParseAnd(depth);
            while (_kind == MarkerToken.Identifier && _token == "or")
            {
                Next();
                var left = expression;
                var right = ParseAnd(depth);
                expression = () => left() || right();
            }
            return expression;
        }
        private Func<bool> ParseAnd(int depth)
        {
            var expression = ParseAtom(depth);
            while (_kind == MarkerToken.Identifier && _token == "and")
            {
                Next();
                var left = expression;
                var right = ParseAtom(depth);
                expression = () => left() && right();
            }
            return expression;
        }
        private Func<bool> ParseAtom(int depth)
        {
            if (_kind == MarkerToken.Open)
            {
                if (depth >= 32) throw UnsupportedDependencyMarker();
                Next();
                var expression = ParseOr(depth + 1);
                if (_kind != MarkerToken.Close) throw UnsupportedDependencyMarker();
                Next();
                return expression;
            }
            var left = Operand();
            if (_kind != MarkerToken.Operator) throw UnsupportedDependencyMarker();
            var operation = _token;
            Next();
            var right = Operand();
            if (left.Variable == right.Variable) throw UnsupportedDependencyMarker();
            var variable = left.Variable ? left.Value : right.Value;
            var literal = left.Variable ? right.Value : left.Value;
            var value = variable switch
            {
                "extra" => extra,
                "implementation_name" => "cpython",
                "platform_python_implementation" => "CPython",
                "sys_platform" => "win32",
                "os_name" => "nt",
                "platform_system" => "Windows",
                "platform_machine" => "AMD64",
                "python_full_version" => pythonVersion,
                "python_version" => string.Join('.', pythonVersion.Split('.').Take(2)),
                _ => throw UnsupportedDependencyMarker()
            };
            var versionComparison = variable is "python_full_version" or "python_version";
            if (versionComparison)
            {
                if (!Regex.IsMatch(literal, @"^[0-9]{1,5}(?:\.[0-9]{1,9}){0,3}\z", RegexOptions.CultureInvariant))
                    throw UnsupportedDependencyMarker();
                if (!literal.Contains('.')) literal += ".0";
                literal = NormalizeVersion(literal);
            }
            else
            {
                if (operation is not ("==" or "!=")) throw UnsupportedDependencyMarker();
                if (variable == "extra") literal = NormalizeExtra(literal);
            }
            return () =>
            {
                var compared = versionComparison ? CompareVersions(value, literal) : string.CompareOrdinal(value, literal);
                if (!left.Variable) compared = -compared;
                return operation switch
                {
                    "==" => compared == 0, "!=" => compared != 0,
                    "<" => compared < 0, "<=" => compared <= 0,
                    ">" => compared > 0, ">=" => compared >= 0,
                    _ => throw UnsupportedDependencyMarker()
                };
            };
        }
        private (bool Variable, string Value) Operand()
        {
            if (_kind is not (MarkerToken.Identifier or MarkerToken.Literal)) throw UnsupportedDependencyMarker();
            var result = (_kind == MarkerToken.Identifier, _token);
            Next();
            return result;
        }
        private void Next()
        {
            if (++_tokens > 512) throw UnsupportedDependencyMarker();
            while (_position < text.Length && text[_position] is ' ' or '\t') _position++;
            if (_position == text.Length) { _kind = MarkerToken.End; _token = ""; return; }
            var start = _position;
            var character = text[_position++];
            if (character is '(' or ')')
            {
                _kind = character == '(' ? MarkerToken.Open : MarkerToken.Close;
                _token = character.ToString();
            }
            else if (character is '\'' or '"')
            {
                while (_position < text.Length && text[_position] != character)
                {
                    if (text[_position] is < ' ' or > '~' or '\\') throw UnsupportedDependencyMarker();
                    _position++;
                }
                if (_position == text.Length) throw UnsupportedDependencyMarker();
                _kind = MarkerToken.Literal;
                _token = text[(start + 1).._position++];
            }
            else if (char.IsAsciiLetter(character) || character == '_')
            {
                while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] == '_')) _position++;
                _kind = MarkerToken.Identifier;
                _token = text[start.._position];
            }
            else if (character is '=' or '!' or '<' or '>')
            {
                if (_position < text.Length && text[_position] == '=') _position++;
                _kind = MarkerToken.Operator;
                _token = text[start.._position];
                if (_token is not ("==" or "!=" or "<" or "<=" or ">" or ">=")) throw UnsupportedDependencyMarker();
            }
            else throw UnsupportedDependencyMarker();
        }
        private enum MarkerToken { End, Identifier, Literal, Operator, Open, Close }
    }
    internal static string SafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 230 || value.StartsWith('/') || value.StartsWith('\\') ||
            value.Contains('\\') || value.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
            throw new InvalidDataException("An update archive contains an unsafe path.");
        var parts = value.TrimEnd('/').Split('/');
        foreach (var part in parts)
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                Regex.IsMatch(part.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("An update archive contains an unsafe path component.");
        return string.Join('/', parts);
    }
    internal static string ChildPath(string root, string relative)
    {
        var safe = SafeRelativePath(relative);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(prefix, safe.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An update path escaped its work folder.");
        FileBoundary.EnsureRegularPath(result);
        return result;
    }
    internal static void ExtractArchive(string archivePath, string destination, long maximumExpandedBytes,
        Func<string, bool> allowedPath, int maximumEntries = 12000, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileBoundary.EnsureRegularPath(archivePath);
        FileBoundary.EnsureRegularPath(destination);
        using var source = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read);
        if (archive.Entries.Count == 0 || archive.Entries.Count > maximumEntries) throw new InvalidDataException("The update archive has too many files or is empty.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        // Validate the complete archive before writing any member.
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = SafeRelativePath(entry.FullName);
            var isDirectory = entry.FullName.EndsWith('/');
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixType is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Links and special files are not accepted in updates.");
            if (!seen.Add(relative) || !allowedPath(relative)) throw new InvalidDataException("The update archive contains duplicate or unexpected paths.");
            if (!isDirectory) filePaths.Add(relative);
            if (entry.Length < 0 || entry.Length > maximumExpandedBytes) throw new InvalidDataException("An update archive member is too large.");
            expanded = checked(expanded + entry.Length);
            if (expanded > maximumExpandedBytes) throw new InvalidDataException("The expanded update archive is too large.");
        }
        foreach (var path in seen)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slash = path.LastIndexOf('/');
            while (slash >= 0)
            {
                var parent = path[..slash];
                if (filePaths.Contains(parent)) throw new InvalidDataException("The update archive has conflicting file and directory paths.");
                slash = parent.LastIndexOf('/');
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ChildPath(destination, entry.FullName);
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            FileBoundary.EnsureRegularPath(target);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[65536];
            long copied = 0;
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                copied = checked(copied + count);
                if (copied > entry.Length) throw new InvalidDataException("An update archive exceeded its declared size.");
                output.Write(buffer, 0, count);
            }
            if (copied != entry.Length) throw new InvalidDataException("An update archive member is truncated.");
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
    internal static string HashFile(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileBoundary.EnsureRegularPath(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        FileBoundary.EnsureRegularPath(path);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            digest.AppendData(buffer, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }
    internal static List<ToolchainFile> Inventory(string root, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = new List<ToolchainFile>();
        Visit(root);
        return files.OrderBy(item => item.Path, StringComparer.Ordinal).ToList();
        void Visit(string folder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileBoundary.EnsureRegularPath(folder);
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileBoundary.EnsureRegularPath(entry);
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) { Visit(entry); continue; }
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (relative == "manifest.json") continue;
                SafeRelativePath(relative);
                var length = new FileInfo(entry).Length;
                if (length > 250L * 1024 * 1024 || files.Count >= 15000) throw new InvalidDataException("The installed update exceeds its file limits.");
                files.Add(new(relative, length, HashFile(entry, cancellationToken)));
            }
        }
    }
}
