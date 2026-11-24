using static TerraformGenerator.Domain.Catalog;

namespace TerraformGenerator.Domain;

// The pipeline for the apps' own code: on every change under apps/<name>, GitHub Actions builds the app and deploys it
// to what the drawing created. Containers are built into their registry and restarted; App Service apps, function apps
// and static sites get their package. Lambda and Cloud Functions code already deploys with the infrastructure.
// The names (which carry a random suffix) come from the Terraform outputs.
public static class AppPipeline
{
    public const string Key = "appPipeline";

    public static PropertyDefinition Setting() =>
        new(Key, "Deploy the apps' code from GitHub", PropertyType.Select, "none",
            [O("none", "No, I deploy the code myself"), O("github", "Yes: each app's code in apps/<name>, built and deployed on every change")], null,
            "Uses the access of Deploy from GitHub. Containers are built into their registry; Lambda and Cloud Functions code deploys with the infrastructure.",
            "Code and deployment");

    public static bool On(SketchModel model) => model.Setting(Key) == "github";

    // Python and Node.js App Service apps are built by App Service when their code arrives.
    public static bool BuildsOnArrival(SketchModel model, SketchResource app) =>
        On(model) && app.Kind == Kinds.WebApp && app.P["runtime"] is "python312" or "node20" or "node22";

    // The apps the pipeline deploys: (box, what it does).
    public static List<SketchResource> Apps(SketchModel model) => model.Resources.Where(app => app.Kind switch
    {
        Kinds.ContainerApp => model.Targets(app, Kinds.ContainerRegistry).Any(),
        Kinds.WebApp => app.P["runtime"] != "docker" || model.Targets(app, Kinds.ContainerRegistry).Any(),
        Kinds.FunctionApp or Kinds.StaticWebApp => true,
        CloudKinds.Fargate => model.Targets(app, CloudKinds.Ecr).Any(),
        CloudKinds.CloudRun => model.Targets(app, CloudKinds.ArtifactRegistry).Any(),
        _ => false,
    }).ToList();

    private static string Output(string name) => Expression($"fromJSON(needs.names.outputs.json).{name}");

    // A GitHub Actions expression: ${{ ... }}.
    private static string Expression(string inside) => "${{ " + inside + " }}";

    private static string Azure(SketchModel model, SketchResource app)
    {
        var folder = $"apps/{app.Name}";
        var label = app.Label;
        var registry = model.Targets(app, Kinds.ContainerRegistry).FirstOrDefault();
        if (registry is not null)
        {
            var image = app.Kind == Kinds.ContainerApp ? app.P["image"] : app.P["dockerImage"];
            var restart = app.Kind == Kinds.ContainerApp
                ? $"az containerapp update --name {Output($"{label}_name")} --resource-group {Output("resource_group_name")} --image {Output($"{registry.Label}_login_server")}/{image} --revision-suffix run{Expression("github.run_number")}"
                : $"az webapp restart --name {Output($"{label}_name")} --resource-group {Output("resource_group_name")}";
            return $"""
                      - name: Build the image in the registry
                        run: az acr build --registry {Output($"{registry.Label}_registry_name")} --image {image} {folder}
                      - name: Start the new image
                        run: {restart}
                """;
        }

        if (app.Kind == Kinds.StaticWebApp)
        {
            return $"""
                      - name: Read the site's deployment token
                        id: token
                        run: |
                          token=$(az staticwebapp secrets list --name {Output($"{label}_name")} --query properties.apiKey -o tsv)
                          echo "::add-mask::$token"
                          echo "value=$token" >> "$GITHUB_OUTPUT"
                      - uses: Azure/static-web-apps-deploy@v1
                        with:
                          azure_static_web_apps_api_token: {Expression("steps.token.outputs.value")}
                          repo_token: {Expression("secrets.GITHUB_TOKEN")}
                          action: upload
                          app_location: {folder}
                          output_location: ''
                """;
        }

        var runtime = app.P["runtime"];
        var (setup, build, package) = runtime switch
        {
            var dotnet when dotnet.StartsWith("dotnet", StringComparison.Ordinal) =>
                ($"""
                      - uses: actions/setup-dotnet@v4
                        with:
                          dotnet-version: '{dotnet[6..]}.0.x'
                """, $"dotnet publish {folder} -c Release -o publish/{app.Name}", $"publish/{app.Name}"),
            var node when node.StartsWith("node", StringComparison.Ordinal) =>
                ($"""
                      - uses: actions/setup-node@v4
                        with:
                          node-version: '{node[4..]}'
                """, $"npm ci --prefix {folder} && npm run build --if-present --prefix {folder}", folder),
            _ =>
                ($"""
                      - uses: actions/setup-python@v5
                        with:
                          python-version: '3.{runtime[7..]}'
                """, "", folder),
        };
        var buildStep = build.Length == 0 ? "" : $"      - name: Build\n        run: {build}\n";
        var deploy = app.Kind == Kinds.FunctionApp
            ? $"""
                      - uses: Azure/functions-action@v1
                        with:
                          app-name: {Output($"{label}_name")}
                          package: {package}
                          scm-do-build-during-deployment: {(runtime.StartsWith("dotnet", StringComparison.Ordinal) ? "false" : "true")}
                          enable-oryx-build: {(runtime.StartsWith("dotnet", StringComparison.Ordinal) ? "false" : "true")}
                """
            : $"""
                      - uses: azure/webapps-deploy@v3
                        with:
                          app-name: {Output($"{label}_name")}
                          package: {package}
                """;
        return setup + "\n" + buildStep + deploy;
    }

    private static string Aws(SketchResource app) => $"""
              - uses: aws-actions/amazon-ecr-login@v2
              - name: Build and push the image
                run: |
                  docker build -t "{Output($"{app.Label}_image")}" apps/{app.Name}
                  docker push "{Output($"{app.Label}_image")}"
              - name: Start the new image
                run: aws ecs update-service --cluster {Output("ecs_cluster")} --service {Output($"{app.Label}_service")} --force-new-deployment
        """;

    private static string Gcp(SketchModel model, SketchResource app) => $"""
              - uses: google-github-actions/setup-gcloud@v2
              - run: gcloud auth configure-docker {model.Settings.Region}-docker.pkg.dev --quiet
              - name: Build and push the image
                run: |
                  docker build -t "{Output($"{app.Label}_image")}" apps/{app.Name}
                  docker push "{Output($"{app.Label}_image")}"
              - name: Start the new image
                run: gcloud run deploy {Output($"{app.Label}_service")} --image "{Output($"{app.Label}_image")}" --region {model.Settings.Region} --quiet
        """;

    private const string AzureSignIn = """
              - uses: azure/login@v2
                with:
                  client-id: ${{ secrets.AZURE_CLIENT_ID }}
                  tenant-id: ${{ secrets.AZURE_TENANT_ID }}
                  subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}
        """;

    private const string AzureEnvironment =
        "  ARM_CLIENT_ID: ${{ secrets.AZURE_CLIENT_ID }}\n  ARM_TENANT_ID: ${{ secrets.AZURE_TENANT_ID }}\n  ARM_SUBSCRIPTION_ID: ${{ secrets.AZURE_SUBSCRIPTION_ID }}\n  ARM_USE_OIDC: 'true'\n";

    private const string AzureInit = """
              - name: Init
                run: >-
                  terraform init -input=false
                  -backend-config="resource_group_name=${{ vars.TF_STATE_RESOURCE_GROUP }}"
                  -backend-config="storage_account_name=${{ vars.TF_STATE_STORAGE_ACCOUNT }}"
                  -backend-config="container_name=tfstate"
                  -backend-config="key=__NAME__-__ENV__.tfstate"
                  -backend-config="use_azuread_auth=true"
                  -backend-config="use_oidc=true"
        """;

    public static string Workflow(SketchModel model)
    {
        var azure = model.IsAzure;
        var signIn = azure ? AzureSignIn : CloudScripts.SignIn(model);
        var init = azure ? AzureInit : CloudScripts.Init(model);
        var jobs = string.Join("\n", Apps(model).Select(app => $"""
              {app.Label.Replace('_', '-')}:
                needs: names
                runs-on: ubuntu-latest
                environment: __ENV__
                steps:
                  - uses: actions/checkout@v4
            {signIn.TrimEnd()}
            {(azure ? Azure(model, app) : model.Platform == Platforms.Aws ? Aws(app) : Gcp(model, app)).TrimEnd()}

            """));
        var text = $"""
            # Builds and deploys the apps' code (apps/<name>) to __NAME__ (__ENV__) on every change on main.
            # It signs in with the access setup-github.ps1 created and reads the apps' names from the Terraform outputs.
            name: Apps (__NAME__ __ENV__)

            on:
              push:
                branches: [main]
                paths: ['apps/**']
              workflow_dispatch:

            permissions:
              id-token: write
              contents: read

            env:
            {(azure ? AzureEnvironment : "")}  TF_IN_AUTOMATION: 'true'

            jobs:
              names:
                runs-on: ubuntu-latest
                outputs:
                  json: {Expression("steps.read.outputs.json")}
                defaults:
                  run:
                    working-directory: infra
                steps:
                  - uses: actions/checkout@v4
            {signIn.TrimEnd()}
                  - uses: hashicorp/setup-terraform@v3
                    with:
                      terraform_wrapper: false
            {init.TrimEnd()}
                  - name: Read the names
                    id: read
                    run: echo "json=$(terraform output -json | jq -c 'map_values(.value)')" >> "$GITHUB_OUTPUT"

            {jobs}
            """;
        return text.Replace("__NAME__", model.Settings.Name, StringComparison.Ordinal)
            .Replace("__ENV__", model.Settings.Environment, StringComparison.Ordinal)
            .Replace("__REGION__", model.Settings.Region, StringComparison.Ordinal)
            .ReplaceLineEndings("\n").TrimEnd() + "\n";
    }

    // The README section: where each app's code goes and what happens on a change.
    public static IEnumerable<string> ReadmeLines(SketchModel model)
    {
        if (!On(model))
        {
            yield break;
        }

        yield return "## Deploy the apps' code from GitHub";
        yield return "";
        yield return "`.github/workflows/apps.yml` builds and deploys each app when its code changes on main. Put each app's code in its folder of the repository:";
        yield return "";
        foreach (var app in Apps(model))
        {
            var what = app.Kind is Kinds.ContainerApp or CloudKinds.Fargate or CloudKinds.CloudRun || (app.Kind == Kinds.WebApp && app.P["runtime"] == "docker")
                ? "a Dockerfile at its root; the image is built into its registry and the app restarts with it"
                : app.Kind == Kinds.StaticWebApp ? "the site; it is built and uploaded" : "the code; it is built and deployed";
            yield return $"- `apps/{app.Name}`: {what}.";
        }

        yield return "";
        yield return "It reads the apps' names from the Terraform outputs, so deploy the infrastructure first. Lambda and Cloud Functions code lives in `functions/` and deploys with the infrastructure.";
        yield return "";
    }

    public static IEnumerable<SketchMessage> Check(SketchModel model)
    {
        if (!On(model))
        {
            yield break;
        }

        if (!model.GitHub)
        {
            yield return new(MessageLevel.Error, "Deploying the apps' code from GitHub uses the access of Deploy from GitHub: turn that on too.");
        }

        if (!model.Terraform)
        {
            yield return new(MessageLevel.Error, "Deploying the apps' code reads the names from the Terraform outputs: choose Terraform (or both) as the code.");
        }

        if (Apps(model).Count == 0)
        {
            yield return new(MessageLevel.Warning, "No app has code for the pipeline to deploy: containers need an arrow to their registry.");
        }
    }
}
