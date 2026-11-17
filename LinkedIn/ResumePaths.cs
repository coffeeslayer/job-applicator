public static class ResumePaths
{
    public const string ResumeFileName = "resume.md";

    public static string GetResumePath(string projectRoot) =>
        Path.Combine(projectRoot, ResumeFileName);

    public static string FindProjectRoot(params string[] startingPaths)
    {
        foreach (var startingPath in startingPaths)
        {
            var directory = new DirectoryInfo(Path.GetFullPath(startingPath));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "LinkedInJobSearch.csproj")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate LinkedInJobSearch.csproj to resolve the project-root resume.md file.");
    }
}
