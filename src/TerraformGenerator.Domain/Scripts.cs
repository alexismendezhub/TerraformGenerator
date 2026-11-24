namespace TerraformGenerator.Domain;

// The PowerShell scripts and the GitHub workflow that go with the generated code. Templates use __NAME__-style
// placeholders so PowerShell's own braces and $ stay readable.
public static class Scripts
{
    private static string Fill(string template, SketchModel model, params (string Key, string Value)[] extra)
    {
        // The extra pieces first: they can hold the common placeholders too.
        var text = template;
        foreach (var (key, value) in extra)
        {
            text = text.Replace(key, value, StringComparison.Ordinal);
        }

        return text
            .Replace("__NAME__", model.Settings.Name, StringComparison.Ordinal)
            .Replace("__ENV__", model.Settings.Environment, StringComparison.Ordinal)
            .Replace("__REGION__", model.Settings.Region, StringComparison.Ordinal)
            .Replace("__RG__", model.ResourceGroupName, StringComparison.Ordinal)
            .Replace("__STATE_RG__", model.StateResourceGroup, StringComparison.Ordinal)
            .ReplaceLineEndings("\n");
    }

    // The Azure services (resource providers) this design uses; a new subscription must register them once.
    public static List<string> Providers(SketchModel model)
    {
        var namespaces = new SortedSet<string>(StringComparer.Ordinal) { "Microsoft.Resources" };
        if (TerraformGenerator.Logs(model))
        {
            namespaces.Add("Microsoft.OperationalInsights");
            namespaces.Add("Microsoft.Insights");
        }

        if (model.Alerts)
        {
            namespaces.Add("Microsoft.Insights");
        }

        if (model.Budget > 0)
        {
            namespaces.Add("Microsoft.Consumption");
        }

        if (model.NeedsNetwork)
        {
            namespaces.Add("Microsoft.Network");
        }

        if (Protection.On(model))
        {
            namespaces.Add("Microsoft.Security");
            namespaces.Add("Microsoft.Insights");
        }

        foreach (var kind in model.Resources.Select(resource => resource.Kind).Distinct())
        {
            namespaces.UnionWith(kind switch
            {
                Kinds.WebApp or Kinds.FunctionApp or Kinds.StaticWebApp => ["Microsoft.Web", "Microsoft.Storage"],
                Kinds.ContainerApp => ["Microsoft.App", "Microsoft.ManagedIdentity"],
                Kinds.SqlDatabase => ["Microsoft.Sql"],
                Kinds.Postgres => ["Microsoft.DBforPostgreSQL"],
                Kinds.CosmosDb => ["Microsoft.DocumentDB"],
                Kinds.Storage => ["Microsoft.Storage"],
                Kinds.Redis => ["Microsoft.Cache"],
                Kinds.ServiceBus => ["Microsoft.ServiceBus"],
                Kinds.KeyVault => ["Microsoft.KeyVault"],
                Kinds.ContainerRegistry => ["Microsoft.ContainerRegistry"],
                Kinds.FrontDoor => ["Microsoft.Cdn"],
                Kinds.AiFoundry or Kinds.Speech => ["Microsoft.CognitiveServices"],
                Kinds.DnsZone => ["Microsoft.Network"],
                AzureServices.AiService => ["Microsoft.CognitiveServices"],
                AzureServices.Search => ["Microsoft.Search"],
                AzureServices.EventHubs => ["Microsoft.EventHub"],
                AzureServices.EventGrid => ["Microsoft.EventGrid"],
                AzureServices.AppConfig => ["Microsoft.AppConfiguration"],
                AzureServices.SignalR or AzureServices.WebPubSub => ["Microsoft.SignalRService"],
                AzureServices.Maps => ["Microsoft.Maps"],
                AzureServices.Communication => ["Microsoft.Communication"],
                AzureServices.MachineLearning => ["Microsoft.MachineLearningServices", "Microsoft.KeyVault", "Microsoft.Storage", "Microsoft.Insights", "Microsoft.OperationalInsights"],
                AzureServices.Bot => ["Microsoft.BotService", "Microsoft.ManagedIdentity"],
                AzureServices.VideoIndexer => ["Microsoft.VideoIndexer", "Microsoft.Storage", "Microsoft.ManagedIdentity"],
                _ => [],
            });
        }

        return namespaces.ToList();
    }

    public const string GitIgnore = """
        # Terraform working files and secrets: never commit them.
        .terraform/
        *.tfstate
        *.tfstate.*
        tfplan
        crash.log
        terraform.tfvars
        backend.hcl
        .build/
        # Keep .terraform.lock.hcl: it pins the provider versions.

        """;

    public static string CheckPrerequisites(SketchModel model)
    {
        if (!model.IsAzure)
        {
            return CloudScripts.CheckPrerequisites(model);
        }

        var tools = new List<string>
        {
            "Need 'az' 'winget install Microsoft.AzureCLI'",
        };
        if (model.Terraform)
        {
            tools.Add("Need 'terraform' 'winget install Hashicorp.Terraform'");
        }

        var bicep = model.Bicep ? """

            # Bicep comes with the Azure CLI as an extra download.
            az bicep version *> $null
            if ($LASTEXITCODE -ne 0) {
                Write-Host 'Installing Bicep for the Azure CLI...'
                az bicep install --only-show-errors
            }

            """ : "";
        var terraformVersion = model.Terraform ? """

            $version = [version](terraform version -json | ConvertFrom-Json).terraform_version
            if ($version -lt [version]'1.6.0') {
                throw "Terraform $version is too old; 1.6 or later is needed. Update it with: winget upgrade Hashicorp.Terraform"
            }

            """ : "";
        return Fill("""
            # Checks the tools and the Azure sign-in, and registers the Azure services this design uses.
            # deploy.ps1 runs it for you; you can also run it alone: ./check-prereqs.ps1
            $ErrorActionPreference = 'Stop'

            function Need([string]$command, [string]$install) {
                if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
                    throw "$command is not installed. Install it with: $install (then open a new terminal)"
                }
            }

            __TOOLS__
            __TERRAFORM_VERSION____BICEP__
            az account show --only-show-errors *> $null
            if ($LASTEXITCODE -ne 0) {
                Write-Host 'Sign in to Azure in the browser window that opens...'
                az login --only-show-errors | Out-Null
            }
            __SUBSCRIPTION__
            $account = az account show --query '{name:name, id:id, user:user.name}' -o json | ConvertFrom-Json
            Write-Host "Azure subscription: $($account.name) ($($account.id)), signed in as $($account.user)."

            # Each Azure service must be registered once per subscription before it can be used.
            foreach ($namespace in @(__PROVIDERS__)) {
                $state = az provider show --namespace $namespace --query registrationState -o tsv
                if ($state -ne 'Registered') {
                    Write-Host "Registering $namespace (once per subscription, can take a few minutes)..."
                    az provider register --namespace $namespace --wait --only-show-errors
                }
            }

            Write-Host 'Tools and Azure sign-in are ready.'

            """, model,
            ("__TOOLS__", string.Join("\n", tools)),
            ("__TERRAFORM_VERSION__", terraformVersion),
            ("__BICEP__", bicep),
            ("__SUBSCRIPTION__", model.SubscriptionId.Length == 0 ? "" : $$"""

                # The design names its subscription.
                az account set --subscription '{{model.SubscriptionId}}'
                if ($LASTEXITCODE -ne 0) { throw 'You cannot use the subscription {{model.SubscriptionId}}. Check the id, or ask for access to it.' }

                """),
            ("__PROVIDERS__", string.Join(", ", Providers(model).Select(name => $"'{name}'"))));
    }

    private static string AdminVariables(SketchModel model) => !TerraformGenerator.NeedsAdmin(model) || model.AdminGroup ? "" : """

        # The databases get you as their Microsoft Entra administrator unless terraform.tfvars names someone else.
        if (-not $env:TF_VAR_admin_name) {
            $me = az ad signed-in-user show --query '{id:id, upn:userPrincipalName}' -o json | ConvertFrom-Json
            $env:TF_VAR_admin_object_id = $me.id
            $env:TF_VAR_admin_name = $me.upn
            $env:TF_VAR_admin_type = 'User'
        }

        """;

    public static string DeployTerraform(SketchModel model) => Fill("""
        # Creates or updates __NAME__ (__ENV__) on __CLOUD__ with Terraform. From this folder: ./deploy.ps1
        # It shows what will change and asks before changing anything. ./deploy.ps1 -Destroy deletes everything.
        param([switch]$Destroy)
        $ErrorActionPreference = 'Stop'
        Set-Location $PSScriptRoot

        & ./check-prereqs.ps1
        __TFVARS____ADMIN____BACKEND__
        terraform init -input=false __INIT_ARGS__
        if ($LASTEXITCODE -ne 0) { throw 'terraform init failed' }

        if ($Destroy) {
            $answer = Read-Host 'This deletes every resource of __NAME__-__ENV__ and its data. Type __NAME__-__ENV__ to confirm'
            if ($answer -ne '__NAME__-__ENV__') {
                Write-Host 'Nothing was deleted.'
                return
            }

            __UNLOCK__terraform destroy -input=false -auto-approve
            return
        }

        terraform plan -input=false -out tfplan
        if ($LASTEXITCODE -ne 0) { throw 'terraform plan failed' }

        $answer = Read-Host 'Above is what will be created, changed or deleted. Type yes to do it'
        if ($answer -ne 'yes') {
            Write-Host 'Nothing was changed.'
            return
        }

        terraform apply -input=false tfplan
        if ($LASTEXITCODE -ne 0) { throw 'terraform apply failed' }

        Write-Host ''
        Write-Host 'Done. Addresses and names (see README.md for the steps after the first deploy):'
        terraform output

        """, model,
        ("__CLOUD__", model.Cloud.Label),
        ("__TFVARS__", !model.IsAzure ? "" : """

            # terraform.tfvars says which subscription to use; the first run takes the one you are signed in to.
            if (-not (Test-Path terraform.tfvars)) {
                $subscription = az account show --query id -o tsv
                "subscription_id = `"$subscription`"" | Set-Content terraform.tfvars
                Write-Host "Wrote terraform.tfvars for subscription $subscription. To use another one: az account set --subscription <name>, delete terraform.tfvars and run again."
            }

            """),
        ("__ADMIN__", AdminVariables(model)),
        ("__UNLOCK__", model.Lock ? """
            # The delete lock goes first, or Azure refuses to delete anything.
            terraform destroy -input=false -auto-approve -target=azurerm_management_lock.main

            """ : ""),
        ("__BACKEND__", model.RemoteState ? """

            # The state (what Terraform created) lives in Azure; the first run creates its storage.
            if (-not (Test-Path backend.hcl)) {
                & ./bootstrap-state.ps1
            }

            """ : "\n"),
        ("__INIT_ARGS__", model.RemoteState ? "-backend-config=backend.hcl" : ""));

    public static string BootstrapState(SketchModel model) => !model.IsAzure ? CloudScripts.BootstrapState(model) : Fill("""
        # Creates, once, the storage account that keeps the Terraform state, and writes backend.hcl for terraform init.
        # deploy.ps1 runs it when backend.hcl is missing. Versioning and soft delete keep old copies of the state.
        $ErrorActionPreference = 'Stop'
        Set-Location $PSScriptRoot

        $group = '__STATE_RG__'
        az group create --name $group --location '__REGION__' --tags project=__NAME__ purpose=terraform-state --only-show-errors | Out-Null

        $account = az storage account list --resource-group $group --query '[0].name' -o tsv
        if (-not $account) {
            $account = '__NAME__tf' + (Get-Random -Minimum 100000 -Maximum 999999)
            Write-Host "Creating the state storage $account..."
            az storage account create --name $account --resource-group $group --location '__REGION__' `
                --sku Standard_GRS --kind StorageV2 --min-tls-version TLS1_2 `
                --allow-blob-public-access false --allow-shared-key-access false --only-show-errors | Out-Null
        }

        az storage account blob-service-properties update --account-name $account --resource-group $group `
            --enable-versioning true --enable-delete-retention true --delete-retention-days 30 --only-show-errors | Out-Null

        # You read and write the state with your own sign-in (no storage keys).
        $me = az ad signed-in-user show --query id -o tsv
        $scope = az storage account show --name $account --resource-group $group --query id -o tsv
        az role assignment create --assignee-object-id $me --assignee-principal-type User `
            --role 'Storage Blob Data Contributor' --scope $scope --only-show-errors | Out-Null

        # A new role takes a minute or two to work.
        for ($attempt = 1; $attempt -le 12; $attempt++) {
            az storage container create --name tfstate --account-name $account --auth-mode login --only-show-errors *> $null
            if ($LASTEXITCODE -eq 0) { break }
            if ($attempt -eq 12) { throw 'Could not create the tfstate container. Wait a few minutes and run deploy.ps1 again.' }
            Start-Sleep -Seconds 15
        }

        @"
        resource_group_name  = "$group"
        storage_account_name = "$account"
        container_name       = "tfstate"
        key                  = "__NAME__-__ENV__.tfstate"
        use_azuread_auth     = true
        "@ | Set-Content backend.hcl

        Write-Host "The Terraform state will live in $account (resource group $group)."

        """, model);

    public static string DeployBicep(SketchModel model) => Fill("""
        # Creates or updates __NAME__ (__ENV__) on Azure with Bicep. From this folder: ./deploy-bicep.ps1
        # It shows what will change (what-if) and asks before changing anything.
        $ErrorActionPreference = 'Stop'
        Set-Location $PSScriptRoot

        & ./check-prereqs.ps1
        __ADMIN__
        $group = '__RG__'
        __GROUP__

        $parameters = @('bicep/main.bicepparam')
        __ADMIN_PARAMS__
        az deployment group what-if --resource-group $group --template-file bicep/main.bicep --parameters @parameters
        if ($LASTEXITCODE -ne 0) { throw 'what-if failed' }

        $answer = Read-Host 'Above is what will be created, changed or deleted. Type yes to do it'
        if ($answer -ne 'yes') {
            Write-Host 'Nothing was changed.'
            return
        }

        az deployment group create --resource-group $group --template-file bicep/main.bicep --parameters @parameters `
            --query properties.outputs -o json
        if ($LASTEXITCODE -ne 0) { throw 'The deployment failed' }

        """, model,
        ("__ADMIN__", !TerraformGenerator.NeedsAdmin(model) || model.AdminGroup ? "" : """

            # The databases get you as their Microsoft Entra administrator.
            $me = az ad signed-in-user show --query '{id:id, upn:userPrincipalName}' -o json | ConvertFrom-Json

            """),
        ("__ADMIN_PARAMS__", !TerraformGenerator.NeedsAdmin(model) || model.AdminGroup ? "" : "$parameters += @(\"adminObjectId=$($me.id)\", \"adminName=$($me.upn)\")"),
        ("__GROUP__", model.ExistingResourceGroup
            ? "if ((az group exists --name $group) -ne 'true') { throw \"The resource group $group does not exist. Create it or choose another one in Terraform Generator.\" }"
            : "az group create --name $group --location '__REGION__' --tags project=__NAME__ environment=__ENV__ managed-by=bicep --only-show-errors | Out-Null"));

    public static string SetupGitHub(SketchModel model) => Fill("""
        # Lets GitHub Actions deploy __NAME__ (__ENV__) without passwords (OpenID Connect). Run it once:
        #   ./setup-github.ps1 -Repository <owner>/<name>
        # It creates an app registration that trusts that repository, gives it access to the subscription, and saves
        # the values the workflow needs in GitHub (with the gh CLI) or prints them for you to add.
        param([Parameter(Mandatory)][string]$Repository)
        $ErrorActionPreference = 'Stop'
        Set-Location $PSScriptRoot

        & ./check-prereqs.ps1
        $subscription = az account show --query id -o tsv
        $tenant = az account show --query tenantId -o tsv

        $name = '__NAME__-__ENV__-github'
        $appId = az ad app list --display-name $name --query '[0].appId' -o tsv
        if (-not $appId) {
            $appId = az ad app create --display-name $name --query appId -o tsv
            az ad sp create --id $appId --only-show-errors | Out-Null
        }

        # GitHub may sign in for: pushes to main, pull requests, and the __ENV__ environment (which can require approval).
        $subjects = @{ main = "repo:${Repository}:ref:refs/heads/main"; pr = "repo:${Repository}:pull_request"; environment = "repo:${Repository}:environment:__ENV__" }
        $existing = az ad app federated-credential list --id $appId --query '[].name' -o tsv
        foreach ($key in $subjects.Keys) {
            if ($existing -notcontains $key) {
                $file = New-TemporaryFile
                @{ name = $key; issuer = 'https://token.actions.githubusercontent.com'; subject = $subjects[$key]; audiences = @('api://AzureADTokenExchange') } |
                    ConvertTo-Json | Set-Content $file
                az ad app federated-credential create --id $appId --parameters "@$file" --only-show-errors | Out-Null
                Remove-Item $file
            }
        }

        # Contributor creates resources; Role Based Access Control Administrator gives the apps their roles.
        foreach ($role in @('Contributor', 'Role Based Access Control Administrator')) {
            az role assignment create --assignee $appId --role $role --scope "/subscriptions/$subscription" --only-show-errors | Out-Null
        }
        __STATE__
        $secrets = [ordered]@{ AZURE_CLIENT_ID = $appId; AZURE_TENANT_ID = $tenant; AZURE_SUBSCRIPTION_ID = $subscription }
        $variables = [ordered]@{}
        __VARIABLES__
        if (Get-Command gh -ErrorAction SilentlyContinue) {
            $answer = Read-Host "Save these in $Repository with the GitHub CLI now? Type yes"
            if ($answer -eq 'yes') {
                foreach ($key in $secrets.Keys) { gh secret set $key --repo $Repository --body $secrets[$key] }
                foreach ($key in $variables.Keys) { gh variable set $key --repo $Repository --body $variables[$key] }
                Write-Host 'Saved. Push the infra folder to main to deploy.'
                return
            }
        }

        Write-Host "In GitHub, open $Repository > Settings > Secrets and variables > Actions and add:"
        foreach ($key in $secrets.Keys) { Write-Host "  Secret   $key = $($secrets[$key])" }
        foreach ($key in $variables.Keys) { Write-Host "  Variable $key = $($variables[$key])" }

        """, model,
        ("__STATE__", model.RemoteState && model.Terraform ? """

            # The workflow reads and writes the Terraform state too.
            if (-not (Test-Path backend.hcl)) { & ./bootstrap-state.ps1 }
            $backend = Get-Content backend.hcl -Raw
            $stateGroup = [regex]::Match($backend, 'resource_group_name\s*=\s*"([^"]+)"').Groups[1].Value
            $stateAccount = [regex]::Match($backend, 'storage_account_name\s*=\s*"([^"]+)"').Groups[1].Value
            $stateScope = az storage account show --name $stateAccount --resource-group $stateGroup --query id -o tsv
            az role assignment create --assignee $appId --role 'Storage Blob Data Contributor' --scope $stateScope --only-show-errors | Out-Null

            """ : "\n"),
        ("__VARIABLES__", string.Join("\n", WorkflowVariables(model))));

    private static IEnumerable<string> WorkflowVariables(SketchModel model)
    {
        if (model.RemoteState && model.Terraform)
        {
            yield return "$variables.TF_STATE_RESOURCE_GROUP = $stateGroup";
            yield return "$variables.TF_STATE_STORAGE_ACCOUNT = $stateAccount";
        }

        if (TerraformGenerator.NeedsAdmin(model))
        {
            yield return "$me = az ad signed-in-user show --query '{id:id, upn:userPrincipalName}' -o json | ConvertFrom-Json";
            yield return "$variables.ADMIN_OBJECT_ID = $me.id";
            yield return "$variables.ADMIN_NAME = $me.upn";
            yield return "$variables.ADMIN_TYPE = 'User'";
        }
    }

    public static string Workflow(SketchModel model) => model.Terraform ? TerraformWorkflow(model) : BicepWorkflow(model);

    private static string AdminEnvironment(SketchModel model, string indent) => !TerraformGenerator.NeedsAdmin(model) ? "" :
        string.Join("\n", new[]
        {
            "TF_VAR_admin_object_id: ${{ vars.ADMIN_OBJECT_ID }}",
            "TF_VAR_admin_name: ${{ vars.ADMIN_NAME }}",
            "TF_VAR_admin_type: ${{ vars.ADMIN_TYPE || 'User' }}",
        }.Select(line => indent + line)) + "\n";

    private static string TerraformWorkflow(SketchModel model) => Fill("""
        # Plans on every pull request and applies on main, for the files in the infra folder.
        # setup-github.ps1 creates the Azure access and the secrets and variables this workflow reads.
        name: Infrastructure (__NAME__ __ENV__)

        on:
          pull_request:
            paths: ['infra/**']
          push:
            branches: [main]
            paths: ['infra/**']
          workflow_dispatch:

        permissions:
          id-token: write
          contents: read

        env:
          ARM_CLIENT_ID: ${{ secrets.AZURE_CLIENT_ID }}
          ARM_TENANT_ID: ${{ secrets.AZURE_TENANT_ID }}
          ARM_SUBSCRIPTION_ID: ${{ secrets.AZURE_SUBSCRIPTION_ID }}
          ARM_USE_OIDC: 'true'
          TF_VAR_subscription_id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}
        __ADMIN__  TF_IN_AUTOMATION: 'true'

        defaults:
          run:
            working-directory: infra

        jobs:
          plan:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@v4
              - uses: hashicorp/setup-terraform@v3
              - name: Init
                run: >-
                  terraform init -input=false
                  -backend-config="resource_group_name=${{ vars.TF_STATE_RESOURCE_GROUP }}"
                  -backend-config="storage_account_name=${{ vars.TF_STATE_STORAGE_ACCOUNT }}"
                  -backend-config="container_name=tfstate"
                  -backend-config="key=__NAME__-__ENV__.tfstate"
                  -backend-config="use_azuread_auth=true"
                  -backend-config="use_oidc=true"
              - run: terraform fmt -check
              - run: terraform validate
              - run: terraform plan -input=false -out tfplan
              - uses: actions/upload-artifact@v4
                with:
                  name: tfplan
                  path: infra/tfplan

          apply:
            if: github.event_name != 'pull_request' && github.ref == 'refs/heads/main'
            needs: plan
            runs-on: ubuntu-latest
            # Add required reviewers to this environment in GitHub to approve each apply.
            environment: __ENV__
            steps:
              - uses: actions/checkout@v4
              - uses: hashicorp/setup-terraform@v3
              - name: Init
                run: >-
                  terraform init -input=false
                  -backend-config="resource_group_name=${{ vars.TF_STATE_RESOURCE_GROUP }}"
                  -backend-config="storage_account_name=${{ vars.TF_STATE_STORAGE_ACCOUNT }}"
                  -backend-config="container_name=tfstate"
                  -backend-config="key=__NAME__-__ENV__.tfstate"
                  -backend-config="use_azuread_auth=true"
                  -backend-config="use_oidc=true"
              - uses: actions/download-artifact@v4
                with:
                  name: tfplan
                  path: infra
              - run: terraform apply -input=false tfplan

        """, model, ("__ADMIN__", AdminEnvironment(model, "  ")));

    private static string BicepWorkflow(SketchModel model) => Fill("""
        # Previews on every pull request and deploys on main, for the files in the infra folder.
        # setup-github.ps1 creates the Azure access and the secrets this workflow reads.
        name: Infrastructure (__NAME__ __ENV__)

        on:
          pull_request:
            paths: ['infra/**']
          push:
            branches: [main]
            paths: ['infra/**']
          workflow_dispatch:

        permissions:
          id-token: write
          contents: read

        defaults:
          run:
            working-directory: infra

        jobs:
          deploy:
            runs-on: ubuntu-latest
            environment: __ENV__
            steps:
              - uses: actions/checkout@v4
              - uses: azure/login@v2
                with:
                  client-id: ${{ secrets.AZURE_CLIENT_ID }}
                  tenant-id: ${{ secrets.AZURE_TENANT_ID }}
                  subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}
        __GROUP_STEP__
              - name: Preview
                run: az deployment group what-if --resource-group __RG__ --template-file bicep/main.bicep --parameters bicep/main.bicepparam__ADMIN__
              - name: Deploy
                if: github.event_name != 'pull_request' && github.ref == 'refs/heads/main'
                run: az deployment group create --resource-group __RG__ --template-file bicep/main.bicep --parameters bicep/main.bicepparam__ADMIN__

        """, model,
        ("__ADMIN__", TerraformGenerator.NeedsAdmin(model)
            ? " adminObjectId=${{ vars.ADMIN_OBJECT_ID }} adminName=${{ vars.ADMIN_NAME }}"
            : ""),
        ("__GROUP_STEP__", model.ExistingResourceGroup
            ? "              # The resource group __RG__ already exists; the deployment only uses it."
            : "              - run: az group create --name __RG__ --location __REGION__ --tags project=__NAME__ environment=__ENV__ managed-by=bicep"));
}
