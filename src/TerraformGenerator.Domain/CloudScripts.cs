namespace TerraformGenerator.Domain;

// The scripts for AWS and Google Cloud: check the tools and the sign-in, and create the bucket for the Terraform state.
public static class CloudScripts
{
    private static string Fill(string template, SketchModel model, params (string Key, string Value)[] extra) => extra
        .Aggregate(template, (text, pair) => text.Replace(pair.Key, pair.Value, StringComparison.Ordinal))
        .Replace("__NAME__", model.Settings.Name, StringComparison.Ordinal)
        .Replace("__ENV__", model.Settings.Environment, StringComparison.Ordinal)
        .Replace("__REGION__", model.Settings.Region, StringComparison.Ordinal)
        .Replace("__PROJECT__", model.GcpProject, StringComparison.Ordinal)
        .ReplaceLineEndings("\n");

    private const string Need = """
        function Need([string]$command, [string]$install) {
            if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
                throw "$command is not installed. Install it with: $install (then open a new terminal)"
            }
        }

        Need 'terraform' 'winget install Hashicorp.Terraform'
        $version = [version](terraform version -json | ConvertFrom-Json).terraform_version
        if ($version -lt [version]'1.10.0') {
            throw "Terraform $version is too old; 1.10 or later is needed. Update it with: winget upgrade Hashicorp.Terraform"
        }

        """;

    public static string CheckPrerequisites(SketchModel model) => model.Platform == Platforms.Aws
        ? Fill("""
            # Checks the tools and the AWS sign-in. deploy.ps1 runs it for you; you can also run it alone: ./check-prereqs.ps1
            $ErrorActionPreference = 'Stop'

            """ + Need + """
            Need 'aws' 'winget install Amazon.AWSCLI'

            # Terraform uses the same sign-in as the AWS CLI (aws configure, aws configure sso, or AWS_PROFILE).
            $identity = aws sts get-caller-identity --output json 2>$null | ConvertFrom-Json
            if (-not $identity) {
                throw 'You are not signed in to AWS. Run "aws configure sso" (or "aws configure" with an access key) and then this script again.'
            }

            Write-Host "AWS account $($identity.Account), signed in as $($identity.Arn)."
            Write-Host 'Tools and AWS sign-in are ready.'

            """, model)
        : Fill("""
            # Checks the tools and the Google Cloud sign-in. deploy.ps1 runs it for you; you can also run it alone: ./check-prereqs.ps1
            $ErrorActionPreference = 'Stop'

            """ + Need + """
            Need 'gcloud' 'winget install Google.CloudSDK'

            # Terraform signs in with Application Default Credentials.
            gcloud auth application-default print-access-token *> $null
            if ($LASTEXITCODE -ne 0) {
                Write-Host 'Sign in to Google Cloud in the browser window that opens...'
                gcloud auth application-default login
            }

            gcloud config set project '__PROJECT__' --quiet *> $null
            gcloud auth application-default set-quota-project '__PROJECT__' --quiet *> $null

            # Terraform calls each Google Cloud service through its API; the basic ones must be on. When Terraform
            # reports another API as disabled, its message has a link that turns it on, or run: gcloud services enable <name>
            gcloud services enable cloudresourcemanager.googleapis.com serviceusage.googleapis.com __SERVICES__ --quiet
            __SQL_AGENT__

            Write-Host "Google Cloud project __PROJECT__, signed in as $(gcloud config get-value account 2>$null)."
            Write-Host 'Tools and Google Cloud sign-in are ready.'

            """, model, ("__SERVICES__", string.Join(' ', GcpGenerator.Services(model))),
        ("__SQL_AGENT__", OwnKeys.Gcp(model) && model.OfKind(CloudKinds.CloudSql).Any()
            ? "# Cloud SQL encrypts with the project's key through its service agent, which must exist first.\ngcloud beta services identity create --service=sqladmin.googleapis.com --project '__PROJECT__' --quiet | Out-Null"
            : ""));

    public static string BootstrapState(SketchModel model) => model.Platform == Platforms.Aws
        ? Fill("""
            # Creates, once, the S3 bucket that keeps the Terraform state, and writes backend.hcl for terraform init.
            # deploy.ps1 runs it when backend.hcl is missing. Versioning keeps old copies of the state; Terraform locks it with a file.
            $ErrorActionPreference = 'Stop'
            Set-Location $PSScriptRoot

            $account = aws sts get-caller-identity --query Account --output text
            $bucket = "__NAME__-tfstate-$account-__REGION__"
            aws s3api head-bucket --bucket $bucket 2>$null
            if ($LASTEXITCODE -ne 0) {
                Write-Host "Creating the state bucket $bucket..."
                if ('__REGION__' -eq 'us-east-1') {
                    aws s3api create-bucket --bucket $bucket --region __REGION__ | Out-Null
                } else {
                    aws s3api create-bucket --bucket $bucket --region __REGION__ --create-bucket-configuration LocationConstraint=__REGION__ | Out-Null
                }
            }

            aws s3api put-bucket-versioning --bucket $bucket --versioning-configuration Status=Enabled
            aws s3api put-public-access-block --bucket $bucket --public-access-block-configuration BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true
            aws s3api put-bucket-encryption --bucket $bucket --server-side-encryption-configuration '{"Rules":[{"ApplyServerSideEncryptionByDefault":{"SSEAlgorithm":"AES256"}}]}'

            @"
            bucket       = "$bucket"
            key          = "__NAME__-__ENV__.tfstate"
            region       = "__REGION__"
            use_lockfile = true
            "@ | Set-Content backend.hcl

            Write-Host "The Terraform state will live in the bucket $bucket."

            """, model)
        : Fill("""
            # Creates, once, the Cloud Storage bucket that keeps the Terraform state, and writes backend.hcl for terraform init.
            # deploy.ps1 runs it when backend.hcl is missing. Versioning keeps old copies of the state; Terraform locks it by itself.
            $ErrorActionPreference = 'Stop'
            Set-Location $PSScriptRoot

            $bucket = '__PROJECT__-__NAME__-tfstate'
            gcloud storage buckets describe "gs://$bucket" *> $null
            if ($LASTEXITCODE -ne 0) {
                Write-Host "Creating the state bucket $bucket..."
                gcloud storage buckets create "gs://$bucket" --project '__PROJECT__' --location '__REGION__' --uniform-bucket-level-access --public-access-prevention
            }

            gcloud storage buckets update "gs://$bucket" --versioning

            @"
            bucket = "$bucket"
            prefix = "__NAME__-__ENV__"
            "@ | Set-Content backend.hcl

            Write-Host "The Terraform state will live in the bucket $bucket."

            """, model);

    // Plans on every pull request and applies on main. The zips of the functions are made while planning, so they
    // travel to the apply job with the plan.
    // The GitHub Actions steps that sign in to the cloud and open the Terraform state.
    public static string SignIn(SketchModel model) => Fill(model.Platform == Platforms.Aws
            ? """
                    - uses: aws-actions/configure-aws-credentials@v4
                      with:
                        role-to-assume: ${{ vars.AWS_ROLE_ARN }}
                        aws-region: __REGION__
              """
            : """
                    - uses: google-github-actions/auth@v2
                      with:
                        workload_identity_provider: ${{ vars.GCP_WORKLOAD_IDENTITY_PROVIDER }}
                        service_account: ${{ vars.GCP_SERVICE_ACCOUNT }}
              """, model);

    public static string Init(SketchModel model) => Fill(model.Platform == Platforms.Aws
            ? """
                    - name: Init
                      run: >-
                        terraform init -input=false
                        -backend-config="bucket=${{ vars.TF_STATE_BUCKET }}"
                        -backend-config="key=__NAME__-__ENV__.tfstate"
                        -backend-config="region=__REGION__"
                        -backend-config="use_lockfile=true"
              """
            : """
                    - name: Init
                      run: >-
                        terraform init -input=false
                        -backend-config="bucket=${{ vars.TF_STATE_BUCKET }}"
                        -backend-config="prefix=__NAME__-__ENV__"
              """, model);

    public static string Workflow(SketchModel model)
    {
        var aws = model.Platform == Platforms.Aws;
        var signIn = SignIn(model);
        var init = Init(model);

        return Fill("""
            # Plans on every pull request and applies on main, for the files in the infra folder.
            # setup-github.ps1 creates the __CLOUD__ access and the variables this workflow reads.
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
              TF_IN_AUTOMATION: 'true'

            defaults:
              run:
                working-directory: infra

            jobs:
              plan:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v4
            __SIGN_IN__
                  - uses: hashicorp/setup-terraform@v3
            __INIT__
                  - run: terraform fmt -check
                  - run: terraform validate
                  - run: terraform plan -input=false -out tfplan
                  - uses: actions/upload-artifact@v4
                    with:
                      name: tfplan
                      include-hidden-files: true
                      path: |
                        infra/tfplan
                        infra/.build

              apply:
                if: github.event_name != 'pull_request' && github.ref == 'refs/heads/main'
                needs: plan
                runs-on: ubuntu-latest
                # Add required reviewers to this environment in GitHub to approve each apply.
                environment: __ENV__
                steps:
                  - uses: actions/checkout@v4
            __SIGN_IN__
                  - uses: hashicorp/setup-terraform@v3
            __INIT__
                  - uses: actions/download-artifact@v4
                    with:
                      name: tfplan
                      path: infra
                  - run: terraform apply -input=false tfplan

            """, model, ("__SIGN_IN__", signIn.TrimEnd()), ("__INIT__", init.TrimEnd()), ("__CLOUD__", aws ? "AWS" : "Google Cloud"));
    }

    private const string SaveVariables = """
        if (Get-Command gh -ErrorAction SilentlyContinue) {
            $answer = Read-Host "Save these in $Repository with the GitHub CLI now? Type yes"
            if ($answer -eq 'yes') {
                foreach ($key in $variables.Keys) { gh variable set $key --repo $Repository --body $variables[$key] }
                Write-Host 'Saved. Push the infra folder to main to deploy.'
                return
            }
        }

        Write-Host "In GitHub, open $Repository > Settings > Secrets and variables > Actions > Variables and add:"
        foreach ($key in $variables.Keys) { Write-Host "  $key = $($variables[$key])" }

        """;

    // Lets GitHub Actions deploy without passwords: GitHub's sign-in (OpenID Connect) is trusted for this repository's
    // main branch, pull requests and environment, and the values the workflow needs are saved in GitHub.
    public static string SetupGitHub(SketchModel model) => Fill(model.Platform == Platforms.Aws
        ? """
            # Lets GitHub Actions deploy __NAME__ (__ENV__) to AWS without passwords (OpenID Connect). Run it once:
            #   ./setup-github.ps1 -Repository <owner>/<name>
            # It creates a role that only that repository can use, and saves the values the workflow needs in GitHub
            # (with the gh CLI) or prints them for you to add.
            param([Parameter(Mandatory)][string]$Repository)
            $ErrorActionPreference = 'Stop'
            Set-Location $PSScriptRoot

            & ./check-prereqs.ps1
            if (-not (Test-Path backend.hcl)) { & ./bootstrap-state.ps1 }
            $bucket = [regex]::Match((Get-Content backend.hcl -Raw), 'bucket\s*=\s*"([^"]+)"').Groups[1].Value
            $account = aws sts get-caller-identity --query Account --output text

            # GitHub's sign-in, trusted once per AWS account.
            $provider = "arn:aws:iam::${account}:oidc-provider/token.actions.githubusercontent.com"
            aws iam get-open-id-connect-provider --open-id-connect-provider-arn $provider *> $null
            if ($LASTEXITCODE -ne 0) {
                aws iam create-open-id-connect-provider --url https://token.actions.githubusercontent.com --client-id-list sts.amazonaws.com | Out-Null
            }

            # Only pushes to main, pull requests and the __ENV__ environment of this repository can use the role.
            $trust = @{
                Version = '2012-10-17'
                Statement = @(@{
                    Effect = 'Allow'
                    Principal = @{ Federated = $provider }
                    Action = 'sts:AssumeRoleWithWebIdentity'
                    Condition = @{
                        StringEquals = @{ 'token.actions.githubusercontent.com:aud' = 'sts.amazonaws.com' }
                        StringLike = @{ 'token.actions.githubusercontent.com:sub' = @("repo:${Repository}:ref:refs/heads/main", "repo:${Repository}:pull_request", "repo:${Repository}:environment:__ENV__") }
                    }
                })
            } | ConvertTo-Json -Depth 10
            $file = New-TemporaryFile
            Set-Content $file $trust
            $role = '__NAME__-__ENV__-github'
            aws iam get-role --role-name $role *> $null
            if ($LASTEXITCODE -ne 0) {
                aws iam create-role --role-name $role --assume-role-policy-document "file://$file" | Out-Null
            } else {
                aws iam update-assume-role-policy --role-name $role --policy-document "file://$file"
            }
            Remove-Item $file

            # The deploy creates roles for the functions and containers, so it needs administrator access.
            # Narrow this policy once the design is stable.
            aws iam attach-role-policy --role-name $role --policy-arn arn:aws:iam::aws:policy/AdministratorAccess

            $variables = [ordered]@{ AWS_ROLE_ARN = "arn:aws:iam::${account}:role/$role"; TF_STATE_BUCKET = $bucket }
            __SAVE__
            """
        : """
            # Lets GitHub Actions deploy __NAME__ (__ENV__) to Google Cloud without passwords (Workload Identity
            # Federation). Run it once:
            #   ./setup-github.ps1 -Repository <owner>/<name>
            # It creates a service account that only that repository can use, and saves the values the workflow needs in
            # GitHub (with the gh CLI) or prints them for you to add.
            param([Parameter(Mandatory)][string]$Repository)
            $ErrorActionPreference = 'Stop'
            Set-Location $PSScriptRoot

            & ./check-prereqs.ps1
            if (-not (Test-Path backend.hcl)) { & ./bootstrap-state.ps1 }
            $bucket = [regex]::Match((Get-Content backend.hcl -Raw), 'bucket\s*=\s*"([^"]+)"').Groups[1].Value
            gcloud services enable iamcredentials.googleapis.com sts.googleapis.com --quiet
            $number = gcloud projects describe '__PROJECT__' --format 'value(projectNumber)'

            # GitHub's sign-in, trusted for this repository only.
            gcloud iam workload-identity-pools describe github --location global --project '__PROJECT__' *> $null
            if ($LASTEXITCODE -ne 0) {
                gcloud iam workload-identity-pools create github --location global --project '__PROJECT__' --display-name 'GitHub Actions' --quiet
            }

            $provider = '__NAME__-__ENV__'
            gcloud iam workload-identity-pools providers describe $provider --workload-identity-pool github --location global --project '__PROJECT__' *> $null
            if ($LASTEXITCODE -ne 0) {
                gcloud iam workload-identity-pools providers create-oidc $provider --workload-identity-pool github --location global --project '__PROJECT__' `
                    --issuer-uri 'https://token.actions.githubusercontent.com' `
                    --attribute-mapping 'google.subject=assertion.sub,attribute.repository=assertion.repository' `
                    --attribute-condition "assertion.repository == '$Repository'" --quiet
            }

            $account = '__NAME__-__ENV__-github'
            $email = "$account@__PROJECT__.iam.gserviceaccount.com"
            gcloud iam service-accounts describe $email --project '__PROJECT__' *> $null
            if ($LASTEXITCODE -ne 0) {
                gcloud iam service-accounts create $account --project '__PROJECT__' --display-name 'GitHub Actions for __NAME__ (__ENV__)' --quiet
            }

            # The deploy creates service accounts and grants roles, so it needs the Owner role.
            # Narrow it once the design is stable.
            gcloud projects add-iam-policy-binding '__PROJECT__' --member "serviceAccount:$email" --role roles/owner --condition None --quiet | Out-Null
            gcloud iam service-accounts add-iam-policy-binding $email --project '__PROJECT__' --role roles/iam.workloadIdentityUser `
                --member "principalSet://iam.googleapis.com/projects/$number/locations/global/workloadIdentityPools/github/attribute.repository/$Repository" --quiet | Out-Null

            $variables = [ordered]@{
                GCP_WORKLOAD_IDENTITY_PROVIDER = "projects/$number/locations/global/workloadIdentityPools/github/providers/$provider"
                GCP_SERVICE_ACCOUNT = $email
                TF_STATE_BUCKET = $bucket
            }
            __SAVE__
            """, model, ("__SAVE__", SaveVariables));
}
