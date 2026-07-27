using System.Text.RegularExpressions;

// Extracts the single ```gherkin fenced block out of each per-feature design
// doc under the source directory and writes it as a generated .feature file
// in the output directory. Markdown is the source of truth (see
// docs/bdd/design/*.md); docs/bdd/features/*.feature is build output — see
// ARCHITECTURE.md -> "Feature-doc extraction tooling".

var sourceDir = args.Length > 0 ? args[0] : Path.Combine("docs", "bdd", "design");
var outputDir = args.Length > 1 ? args[1] : Path.Combine("docs", "bdd", "features");

if (!Directory.Exists(sourceDir))
{
    Console.Error.WriteLine($"Source directory not found: {sourceDir}");
    return 1;
}

Directory.CreateDirectory(outputDir);

var gherkinBlock = new Regex(@"```gherkin\r?\n(.*?)\r?\n```", RegexOptions.Singleline);
var hadError = false;

foreach (var mdFile in Directory.EnumerateFiles(sourceDir, "*.md").OrderBy(f => f, StringComparer.Ordinal))
{
    var stem = Path.GetFileNameWithoutExtension(mdFile);
    var text = File.ReadAllText(mdFile);
    var matches = gherkinBlock.Matches(text);

    if (matches.Count == 0)
    {
        Console.WriteLine($"[skip] {stem}.md has no ```gherkin block yet (still being drafted?)");
        continue;
    }

    if (matches.Count > 1)
    {
        Console.Error.WriteLine($"[error] {stem}.md has {matches.Count} ```gherkin blocks — exactly one is required (ambiguous which is authoritative).");
        hadError = true;
        continue;
    }

    var gherkin = matches[0].Groups[1].Value.Replace("\r\n", "\n").TrimEnd('\n');
    var outputPath = Path.Combine(outputDir, $"{stem}.feature");
    var content =
        "# GENERATED FILE — DO NOT EDIT DIRECTLY.\n" +
        $"# Source: {Path.Combine(sourceDir, stem)}.md\n" +
        "# Regenerated automatically on every build (tools/FeatureDocExtractor).\n" +
        "\n" +
        gherkin + "\n";

    if (File.Exists(outputPath) && File.ReadAllText(outputPath) == content)
    {
        Console.WriteLine($"[unchanged] {stem}.feature");
        continue;
    }

    File.WriteAllText(outputPath, content);
    Console.WriteLine($"[wrote] {stem}.feature");
}

return hadError ? 1 : 0;
