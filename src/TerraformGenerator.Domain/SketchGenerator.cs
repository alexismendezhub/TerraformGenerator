namespace TerraformGenerator.Domain;

// Everything a person who does not know Terraform needs: the code (Terraform, and Bicep for Azure), scripts that check
// the tools, create the state storage and deploy with a confirmation, an optional GitHub pipeline, and a README.
// schemas (the providers' lists of resources) are needed for boxes of any provider resource; without them those boxes
// are written as they are, unchecked.
public static class SketchGenerator
{
    public static GenerationResult Generate(Sketch sketch, IResourceSchemas? schemas = null) =>
        Environments.Several(sketch)
            ? Environments.Generate(sketch, environment => GenerateOne(environment, schemas))
            : GenerateOne(Environments.For(sketch, sketch.Settings.Environment), schemas);

    private static GenerationResult GenerateOne(Sketch sketch, IResourceSchemas? schemas)
    {
        var messages = SketchRules.Check(sketch, schemas);
        if (messages.Any(message => message.Level == MessageLevel.Error))
        {
            return new GenerationResult(new Dictionary<string, string>(), messages);
        }

        var model = new SketchModel(sketch);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (model.Terraform)
        {
            var terraform = model.IsAzure ? TerraformGenerator.Files(model, schemas) : CloudGenerator.Files(model, schemas);
            foreach (var (name, content) in terraform)
            {
                files[name] = content;
            }

            files["deploy.ps1"] = Scripts.DeployTerraform(model);
            if (model.RemoteState)
            {
                files["bootstrap-state.ps1"] = Scripts.BootstrapState(model);
            }
        }

        if (model.Bicep)
        {
            foreach (var (name, content) in BicepGenerator.Files(model))
            {
                files[$"bicep/{name}"] = content;
            }

            files["deploy-bicep.ps1"] = Scripts.DeployBicep(model);
        }

        files["check-prereqs.ps1"] = Scripts.CheckPrerequisites(model);
        files[".gitignore"] = Scripts.GitIgnore;
        if (model.GitHub)
        {
            files[".github/workflows/infrastructure.yml"] = model.IsAzure ? Scripts.Workflow(model) : CloudScripts.Workflow(model);
            files["setup-github.ps1"] = model.IsAzure ? Scripts.SetupGitHub(model) : CloudScripts.SetupGitHub(model);
            if (AppPipeline.On(model))
            {
                files[".github/workflows/apps.yml"] = AppPipeline.Workflow(model);
            }
        }

        files["README.md"] = Readme.Write(model);
        return new GenerationResult(files, messages);
    }
}
