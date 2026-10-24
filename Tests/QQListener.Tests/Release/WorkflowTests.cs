using System.Text.Json;
using System.Xml.Linq;
using YamlDotNet.RepresentationModel;
using Xunit;

namespace QQListener.Tests.Release;

public sealed class WorkflowTests
{
    private const string PackageCondition =
        "runner.os == 'Windows' && github.event_name != 'pull_request'";
    private const string RestoreRun = "dotnet restore QQListener.sln --locked-mode";
    private const string BuildRun =
        "dotnet build QQListener.sln --configuration Release --no-restore -p:ContinuousIntegrationBuild=true";
    private const string TestRun =
        "dotnet test QQListener.sln --configuration Release --no-build --no-restore --results-directory artifacts/test-results --logger trx";
    private const string SamplesRun =
        "dotnet run --project Samples/QQListener.SampleVerifier/QQListener.SampleVerifier.csproj --configuration Release --no-build --no-restore";
    private const string BranchMetadataRun =
        "dotnet run --project Tools/QQListener.ReleaseVerifier/QQListener.ReleaseVerifier.csproj --configuration Release --no-build --no-restore -- --root . --configuration Release --metadata-only";
    private const string TaggedMetadataRun =
        "dotnet run --project Tools/QQListener.ReleaseVerifier/QQListener.ReleaseVerifier.csproj --configuration Release --no-build --no-restore -- --root . --configuration Release --metadata-only --tag \"$env:RELEASE_TAG\"";
    private const string BuildCipxRun =
        "dotnet build QQListener.csproj --configuration Release --no-restore -p:CreateCipx=true -p:ContinuousIntegrationBuild=true";
    private const string BranchPackageRun =
        "dotnet run --project Tools/QQListener.ReleaseVerifier/QQListener.ReleaseVerifier.csproj --configuration Release --no-build --no-restore -- --root . --configuration Release";
    private const string TaggedPackageRun =
        "dotnet run --project Tools/QQListener.ReleaseVerifier/QQListener.ReleaseVerifier.csproj --configuration Release --no-build --no-restore -- --root . --configuration Release --tag \"$env:RELEASE_TAG\"";

    [Fact]
    public void BuildWorkflowIsValidAndPreservesReleaseGateOrder()
    {
        var root = FindProjectRoot();
        var yaml = File.ReadAllText(Path.Combine(root, ".github", "workflows", "build.yml"));
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var document = Assert.Single(stream.Documents);
        var steps = ReadJobSteps(document);

        AssertUsesStep(steps, "Check out repository", "actions/checkout@v4");
        AssertStep(steps, "Restore locked dependencies", null, RestoreRun);
        AssertStep(steps, "Build release", null, BuildRun);
        AssertStep(steps, "Run tests", null, TestRun);
        AssertStep(steps, "Verify toast samples", null, SamplesRun);
        foreach (var metadataStep in new[] { "Verify branch metadata", "Verify tagged metadata" })
        {
            var packageStep = metadataStep == "Verify branch metadata"
                ? "Verify clean install package" : "Verify tagged clean install package";
            AssertNamedStepsOrdered(steps, "Restore locked dependencies", "Build release", "Run tests",
                "Verify toast samples", "Verify message UI", metadataStep, "Build CIPX", packageStep,
                "Upload plugin package", "Upload verification evidence");
        }
        Assert.DoesNotContain("setup-python", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("working-directory: plugin", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("plugin/", yaml, StringComparison.Ordinal);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows"), "*.yml"));

        using var samples = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Samples", "qq-toast-samples.json")));
        Assert.Equal(23, samples.RootElement.GetArrayLength());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void RepositoryWorkflowBuildsVerifiedWindowsPackageAfterCrossPlatformChecks(string lineEnding)
    {
        var root = FindProjectRoot();
        var yaml = File.ReadAllText(Path.Combine(root, ".github", "workflows", "build.yml"))
            .ReplaceLineEndings(lineEnding);
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var document = Assert.Single(stream.Documents);
        var mapping = Assert.IsType<YamlMappingNode>(document.RootNode);
        Assert.Equal("read", Scalar(Mapping(mapping, "permissions"), "contents"));
        var build = Mapping(Mapping(mapping, "jobs"), "build");
        var matrix = Mapping(Mapping(build, "strategy"), "matrix");
        var platforms = Assert.IsType<YamlSequenceNode>(Value(matrix, "os"));
        Assert.Equal(new[] { "windows-latest", "macos-14" },
            platforms.Children.Select(n => Assert.IsType<YamlScalarNode>(n).Value));
        Assert.False(build.Children.ContainsKey(new YamlScalarNode("defaults")));

        var steps = ReadJobSteps(document);
        AssertStep(steps, "Restore locked dependencies", null, RestoreRun);
        AssertStep(steps, "Build release", null, BuildRun);
        AssertStep(steps, "Run tests", null,
            "dotnet test QQListener.sln --configuration Release --no-build --no-restore --results-directory artifacts/test-results --logger trx");
        AssertStep(steps, "Verify toast samples", null, SamplesRun);
        AssertStep(steps, "Verify message UI", null,
            "dotnet run --project Samples/QQListener.UiPreview/QQListener.UiPreview.csproj --configuration Release --no-build --no-restore -- --render artifacts/ui-previews");
        AssertStep(steps, "Verify branch metadata",
            $"{PackageCondition} && github.ref_type != 'tag'", BranchMetadataRun);
        AssertStep(steps, "Verify tagged metadata",
            $"{PackageCondition} && github.ref_type == 'tag'", TaggedMetadataRun);
        AssertStep(steps, "Build CIPX", PackageCondition, BuildCipxRun);
        AssertStep(steps, "Verify clean install package",
            $"{PackageCondition} && github.ref_type != 'tag'", BranchPackageRun);
        AssertStep(steps, "Verify tagged clean install package",
            $"{PackageCondition} && github.ref_type == 'tag'", TaggedPackageRun);
        AssertNamedStepsOrdered(steps, "Restore locked dependencies", "Build release", "Run tests",
            "Verify toast samples", "Verify message UI", "Verify branch metadata", "Build CIPX",
            "Verify clean install package", "Upload plugin package", "Upload verification evidence");
        AssertNamedStepsOrdered(steps, "Restore locked dependencies", "Build release", "Run tests",
            "Verify toast samples", "Verify message UI", "Verify tagged metadata", "Build CIPX",
            "Verify tagged clean install package", "Upload plugin package", "Upload verification evidence");

        var packageUpload = Assert.Single(steps, s => s.Name == "Upload plugin package");
        Assert.Equal(PackageCondition, packageUpload.Condition);
        Assert.Equal("actions/upload-artifact@v4", packageUpload.Uses);
        var evidenceUpload = Assert.Single(steps, s => s.Name == "Upload verification evidence");
        Assert.Equal("always()", evidenceUpload.Condition);
        Assert.Equal("actions/upload-artifact@v4", evidenceUpload.Uses);
        Assert.Contains("global-json-file: global.json", yaml, StringComparison.Ordinal);
        Assert.Contains("cache-dependency-path: '**/packages.*.lock.json'", yaml, StringComparison.Ordinal);
        var triggers = Mapping(mapping, "on");
        foreach (var eventName in new[] { "push", "pull_request" })
        {
            var branches = Assert.IsType<YamlSequenceNode>(Value(Mapping(triggers, eventName), "branches"));
            Assert.Equal("main", Assert.IsType<YamlScalarNode>(Assert.Single(branches.Children)).Value);
        }
        Assert.Contains("'[0-9]*.[0-9]*.[0-9]*'", yaml, StringComparison.Ordinal);
        Assert.Contains("cipx/QQListener.cipx", yaml, StringComparison.Ordinal);
        Assert.Contains("cipx/checksums.md", yaml, StringComparison.Ordinal);
        Assert.Contains("artifacts/test-results/*.trx", yaml, StringComparison.Ordinal);
        Assert.Contains("artifacts/ui-previews/*.png", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain(".dll", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasePublishingRequiresSuccessfulBuildsAndATagPush()
    {
        var root = FindProjectRoot();
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(Path.Combine(root, ".github", "workflows", "build.yml"))));
        var document = Assert.Single(stream.Documents);
        var mapping = Assert.IsType<YamlMappingNode>(document.RootNode);
        Assert.Equal("read", Scalar(Mapping(mapping, "permissions"), "contents"));
        var jobs = Mapping(mapping, "jobs");
        var build = Mapping(jobs, "build");
        Assert.Equal("${{ github.ref_name }}", Scalar(Mapping(build, "env"), "RELEASE_TAG"));
        var release = Mapping(jobs, "release");
        Assert.Equal("build", Scalar(release, "needs"));
        Assert.Equal("github.event_name == 'push' && github.ref_type == 'tag'", Scalar(release, "if"));
        Assert.Equal("write", Scalar(Mapping(release, "permissions"), "contents"));
        Assert.Equal("release-${{ github.ref }}", Scalar(Mapping(release, "concurrency"), "group"));
        Assert.Equal("false", Scalar(Mapping(release, "concurrency"), "cancel-in-progress"));

        var steps = ReadJobSteps(document, "release");
        AssertUsesStep(steps, "Check out tagged source", "actions/checkout@v4");
        AssertUsesStep(steps, "Download verified plugin package", "actions/download-artifact@v4");
        AssertStep(steps, "Publish release assets", null, "bash Tools/PublishRelease.sh release");
        AssertNamedStepsOrdered(steps, "Check out tagged source", "Download verified plugin package", "Publish release assets");
        var stepNodes = Assert.IsType<YamlSequenceNode>(Value(release, "steps")).Children
            .Select(node => Assert.IsType<YamlMappingNode>(node)).ToArray();
        Assert.Equal("false", Scalar(Mapping(stepNodes[0], "with"), "persist-credentials"));
        Assert.Equal("QQListener-${{ github.sha }}", Scalar(Mapping(stepNodes[1], "with"), "name"));
        Assert.Equal("release", Scalar(Mapping(stepNodes[1], "with"), "path"));
        Assert.Equal("${{ github.token }}", Scalar(Mapping(stepNodes[2], "env"), "GH_TOKEN"));
        Assert.Equal("${{ github.ref_name }}", Scalar(Mapping(stepNodes[2], "env"), "RELEASE_TAG"));
        Assert.Equal("bash", Scalar(stepNodes[2], "shell"));
        AssertStep(ReadJobSteps(document), "Verify release automation", null, "bash Tests/Release/run.sh");
        Assert.True(File.Exists(Path.Combine(root, "Tools", "PublishRelease.sh")));
        Assert.True(File.Exists(Path.Combine(root, "Tests", "Release", "run.sh")));
        Assert.Contains("*.sh text eol=lf", File.ReadAllText(Path.Combine(root, ".gitattributes")));
    }

    [Fact]
    public void CipxBuildSelectsWindowsTargetBeforePlatformDefaults()
    {
        var root = FindProjectRoot();
        var properties = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        var frameworkProperties = properties.Descendants("QQListenerTargetFramework").ToArray();

        Assert.Equal("'$(CreateCipx)' == 'true'", (string?)frameworkProperties[0].Attribute("Condition"));
        Assert.Equal("net8.0-windows10.0.19041.0", frameworkProperties[0].Value);
        Assert.All(frameworkProperties.Skip(1), property =>
            Assert.Contains("'$(QQListenerTargetFramework)' == ''", (string?)property.Attribute("Condition")));
        var windowsTargeting = Assert.Single(properties.Descendants("EnableWindowsTargeting"));
        Assert.Equal("'$(CreateCipx)' == 'true'", (string?)windowsTargeting.Attribute("Condition"));
        Assert.Equal("true", windowsTargeting.Value);
    }

    [Fact]
    public void CipxBuildRejectsNonWindowsTargetOverrides()
    {
        var root = FindProjectRoot();
        var project = XDocument.Load(Path.Combine(root, "QQListener.csproj"));
        var validation = Assert.Single(project.Descendants("Target"), target =>
            (string?)target.Attribute("Name") == "ValidateCipxTargetFramework");

        Assert.Equal("PrepareForBuild", (string?)validation.Attribute("BeforeTargets"));
        Assert.Equal("'$(CreateCipx)' == 'true'", (string?)validation.Attribute("Condition"));
        var error = Assert.Single(validation.Elements("Error"));
        Assert.Equal("'$(TargetFramework)' != 'net8.0-windows10.0.19041.0'", (string?)error.Attribute("Condition"));
    }

    private static IReadOnlyList<WorkflowStep> ReadJobSteps(YamlDocument document, string jobName = "build")
    {
        var root = Assert.IsType<YamlMappingNode>(document.RootNode);
        var jobs = Mapping(root, "jobs");
        var build = Mapping(jobs, jobName);
        var sequence = Assert.IsType<YamlSequenceNode>(Value(build, "steps"));
        return sequence.Children
            .Select(node => Assert.IsType<YamlMappingNode>(node))
            .Select(node => new WorkflowStep(
                Scalar(node, "name"),
                OptionalScalar(node, "if"),
                OptionalScalar(node, "run"),
                OptionalScalar(node, "uses")))
            .ToArray();
    }

    private static void AssertStep(
        IReadOnlyList<WorkflowStep> steps,
        string name,
        string? condition,
        string run)
    {
        var step = Assert.Single(steps, candidate => candidate.Name == name);
        Assert.Equal(condition, step.Condition);
        Assert.Equal(run, step.Run);
        Assert.Null(step.Uses);
    }

    private static void AssertUsesStep(
        IReadOnlyList<WorkflowStep> steps,
        string name,
        string uses)
    {
        var step = Assert.Single(steps, candidate => candidate.Name == name);
        Assert.Null(step.Condition);
        Assert.Null(step.Run);
        Assert.Equal(uses, step.Uses);
    }

    private static void AssertNamedStepsOrdered(
        IReadOnlyList<WorkflowStep> steps,
        params string[] names)
    {
        var previous = -1;
        foreach (var name in names)
        {
            var current = steps
                .Select((step, index) => (step, index))
                .Single(item => item.step.Name == name)
                .index;
            Assert.True(current > previous, $"Expected step '{name}' after index {previous}.");
            previous = current;
        }
    }

    private static YamlMappingNode Mapping(YamlMappingNode node, string key) =>
        Assert.IsType<YamlMappingNode>(Value(node, key));

    private static YamlNode Value(YamlMappingNode node, string key)
    {
        Assert.True(node.Children.TryGetValue(new YamlScalarNode(key), out var value));
        return value;
    }

    private static string Scalar(YamlMappingNode node, string key) =>
        Assert.IsType<YamlScalarNode>(Value(node, key)).Value
        ?? throw new InvalidDataException($"Workflow value '{key}' is null.");

    private static string? OptionalScalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value)
            ? Assert.IsType<YamlScalarNode>(value).Value
            : null;

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QQListener.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to find QQListener.sln.");
    }

    private sealed record WorkflowStep(
        string Name,
        string? Condition,
        string? Run,
        string? Uses);
}
