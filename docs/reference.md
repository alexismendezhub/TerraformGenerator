# Terraform Generator: reference

Everything beyond the README: how it is built, how to use and configure it, the API and the tests.

## 1. What it does

Terraform Generator generates Infrastructure as Code (Terraform and Bicep) from a drawing of your cloud architecture.

- **Draw.** Choose the cloud (Azure, AWS or Google Cloud), add boxes from the palette, connect them with arrows and
  choose their options in the side panel. **Search services…** lists every resource of the cloud's official Terraform
  provider; pin the ones you use and they stay in the palette.
- **See the code as you draw.** The code tab shows Terraform (and Bicep on Azure) and updates with every change. Each
  box brings what it needs to work (identity and access, network, settings, logs), so none of it is written by hand.
- **Checks as you draw.** Errors stop the code from being generated, warnings point out risky choices, and notes
  explain what was added for you. **Check the code** runs `terraform validate`, `terraform fmt -check` and the Bicep compiler on your computer, without creating anything in the cloud.
- **Project settings.** Name, environments, region, which code to generate, where Terraform keeps its state, deploying
  from GitHub, and the safeguards a system needs before going live (firewall, alerts, budget, audit).
- **Describe it with AI.** Write what you need and the AI proposes the drawing; it can also review the drawing and fill
  in a box's fields. Nothing changes until you approve it. It uses Claude ([section 7](#7-configuration-reference)).
- **Import Terraform.** Open the `.tf` files of an environment you already have and get the drawing back.
- **Download.** A ZIP with the code, deploy scripts, a step-by-step README and `sketch.json` (the drawing). The drawing
  is also kept in your browser (no database).

## 2. How to run it

**Requirements:** the [.NET 10 SDK](https://dotnet.microsoft.com/download), [Node.js](https://nodejs.org) 20, 22 or 24,
and PowerShell 7 (`pwsh`). No database is needed: the drawing is kept in your browser and in the `sketch.json` you
download, and the palette (pins and section names) in your browser. To check the code from the screen you also need
[Terraform](https://developer.hashicorp.com/terraform/install) (`winget install Hashicorp.Terraform`) and Bicep
(`az bicep install`).

### 2.1 Start it

```cmd
scripts\start.cmd
```

It builds the screen, starts the app at http://localhost:5080 and opens your browser. The app listens only on your
computer.

### 2.2 Visual Studio and live reload

Open `TerraformGenerator.slnx` and start **TerraformGenerator.Server**. For live reload while you work on the screen, run the server
(`dotnet run --project src\TerraformGenerator.Server --urls http://localhost:5080`) and, in `src\terraformgenerator.client`, run `npm start`. Then open
http://localhost:4200. The `/api` calls go to the server.

### 2.3 The AI (optional)

Describing, the AI review and import with AI use Claude. Set your API key before you start the app and open a new
terminal afterwards:

```powershell
setx ANTHROPIC_API_KEY "your-key"
```

Without a key the drawing works the same and the AI buttons are disabled.

## 3. Project structure

```text
TerraformGenerator.slnx                                      ← solution (.slnx format)
├─ src/
│  ├─ TerraformGenerator.Domain/                             ← the catalog, the checks, the HCL and Bicep writers, the Terraform and
│  │                                                       Bicep generators, scripts and README templates, the importer, the AI
│  │                                                       prompts and the proposal builder (no framework dependencies)
│  ├─ TerraformGenerator.Application/                        ← ports: ISketchProposer + AI options
│  ├─ TerraformGenerator.Infrastructure/                     ← implementations
│  │  ├─ Ai/                                             ← the Anthropic (Claude) proposer
│  │  ├─ Terraform/                                      ← terraform runner and formatter, provider schemas
│  │  └─ DependencyInjection.cs                          ← AddInfrastructure
│  ├─ TerraformGenerator.Server/                             ← ASP.NET Core (Minimal APIs); serves the screen from wwwroot
│  │  ├─ Endpoints/SketchEndpoints.cs                    ← every /api endpoint
│  │  ├─ Contracts/                                      ← request records
│  │  └─ Program.cs
│  ├─ terraformgenerator.client/                             ← Angular screen (.esproj): canvas, properties, code, AI review, proposals
│  └─ tests/
│     ├─ TerraformGenerator.Domain.UnitTests/                ← checks, generators, importer (with a round trip), AI parsing, real terraform/bicep
│     └─ TerraformGenerator.Server.IntegrationTests/         ← the endpoints over HTTP
├─ docs/                                                 ← cloud-publishing.md (open findings), Word and PDF copies of this README
└─ scripts/                                              ← start.cmd (builds the screen and starts the app), build-and-test.cmd
```

## 4. Use it

1. **Add resources:** use the boxes on the left, **Describe with AI**, **Import Terraform**, or one of the examples.
2. **Connect them:** drag from the dot on the right edge of a box to another box. Only connections that make sense are allowed; while you drag, the boxes you can connect to are highlighted.
3. **Choose options:** click a box. With nothing selected, the panel shows the project:
   - name, environment and region;
   - whether to generate Terraform, Bicep or both;
   - where the Terraform state lives;
   - deploying from GitHub;
   - a monthly budget alert.
4. **Read the code:** the **Code** tab shows every file as you draw. **Check the code** runs `terraform init` (without a backend), `terraform validate`, `terraform fmt -check` and the Bicep compiler. It never creates anything in the cloud.
5. **Ask the AI:** the **AI review** tab lists recommendations for security, reliability and cost. **Apply as a proposal** shows a recommendation as a proposal you approve or discard.
6. **Download (ZIP):** you get the code, the scripts and `sketch.json` (your drawing). **Save drawing** and **Open** save and load the drawing on its own; it is also kept in your browser. To bring downloaded code back with **Import Terraform**, choose the `.tf` files together with the `sketch.json` of the same folder: every box goes back where it was (each environment's folder has its own copy).

### 4.1 What is in the ZIP

- `README.md`: step-by-step instructions for the downloaded code.
- Deploy scripts (`deploy.ps1`, and `deploy-bicep.ps1` on Azure): they check the tools, sign you in to the cloud, show
  what will change and ask you to type `yes` before changing anything. `-Destroy` removes everything after you type the
  project name.
- A GitHub workflow and its setup script, when you choose deploying from GitHub.
- `.gitignore`, so the Terraform state and your settings never reach Git.
- `sketch.json`: the drawing.

## 5. Proposals: nothing changes until you approve

Describing, importing and applying a recommendation all end in a **proposal**:

- **On the canvas:** green boxes are new, amber boxes change, and a **!** marks a choice made for you that you should confirm.
- **In the panel:** what **needs your review**, the **changes** compared with your drawing, and the usual **checks**.
- **Your decision:**
  - **Ask for changes** corrects the proposal in your words;
  - **Approve** puts it on your drawing and opens the code;
  - **Discard** leaves your drawing as it was.

**Import Terraform** reads the files in one of two ways:

- **With rules**, on this computer. It recognizes the resources above and their connections: app settings, role assignments, Front Door origins, custom domains and private endpoints. It lists what it cannot draw.
- **With AI.** It understands more, and explains each thing it leaves out.

## 6. Adding a resource or an option

The Domain project holds the catalog and the generators. To add a resource or an option:

1. Edit `Catalog.cs` first: the screen and the AI prompts all read it.
2. Generate the resource in `TerraformGenerator.cs` and `BicepGenerator.cs`.
3. Read it in `TerraformImporter.cs`.
4. Add any rules to `SketchRules.cs`.
5. Add tests.

## 7. Configuration reference

### 7.1 The AI (Claude)

Describing, the AI review, and import with AI use Claude. Your description, the drawing, or the imported files are sent to Anthropic. Do not include passwords or keys.

Create an API key at [platform.claude.com](https://platform.claude.com) and set it before you start Terraform Generator:

```powershell
setx ANTHROPIC_API_KEY "your-key"
```

Open a new terminal afterwards so it sees the key. The settings live in `src/TerraformGenerator.Server/appsettings.json` under `Ai`:

| Setting | Default | What it does |
| --- | --- | --- |
| `Provider` | `Anthropic` | `None` turns the AI off (its buttons are disabled; import with rules still works) |
| `Model` | `claude-opus-5-5` | The Claude model |
| `Effort` | `medium` | `low`, `medium`, `high`, `xhigh` or `max`: how hard Claude thinks |
| `ApiKey` | empty | Only if you prefer it to the `ANTHROPIC_API_KEY` variable (use user secrets, never commit it) |

Every request asks for a fixed JSON shape (structured output) and turns on Anthropic's server-side fallback, so a declined request is answered by another Claude model in the same call. The AI review answers in your browser's language.

## 8. API

| Method and path | Description |
|---|---|
| `GET /api/catalog` | The smart boxes, their options and the regions |
| `GET /api/example?name=` | An example drawing (`everything`, `aws`, `gcp`, or the web API with SQL) |
| `GET /api/platforms` | The three clouds and their Terraform providers |
| `GET /api/schemas/{platform}/status`, `POST .../refresh`, `GET .../search?q=`, `GET .../types/{type}` | Every resource of the cloud's Terraform provider |
| `POST /api/generate` | The drawing → Terraform / Bicep files and the checks |
| `POST /api/validate` | `terraform init` (no backend), `validate`, `fmt -check` and the Bicep compiler on the generated files |
| `POST /api/export` | The ZIP: code, scripts and `sketch.json` |
| `GET /api/ai/status` | Whether the AI is on, and why not |
| `POST /api/ai/propose`, `POST /api/ai/recommend` | A proposed drawing from a description; recommendations for the drawing (502 when the AI fails) |
| `POST /api/import` | Existing Terraform → a proposed drawing, with rules or with AI |

Any other `/api/...` path answers 404.

## 9. Tests

`scripts\build-and-test.cmd` runs everything on Windows: `npm audit`, the screen build, the .NET build (warnings as
errors) and the .NET tests.

| Project | Command | Covers |
|---|---|---|
| `TerraformGenerator.Domain.UnitTests` (114) | `dotnet test TerraformGenerator.slnx` | The checks, the generators, the importer (including a round trip) and the AI parsing. With Terraform installed, several generated drawings are checked with real tools: `terraform validate`, `terraform fmt -check`, the Bicep compiler (no warnings allowed) and the PowerShell parser for every script. |
| `TerraformGenerator.Server.IntegrationTests` (6) | same | The endpoints over HTTP with the AI off: catalog, example, generate, the ZIP, AI status and 502, an empty description refused, no preferences endpoint, unknown `/api` paths answer 404. |

## 10. Production and deployment

The app is built as a tool for one person on their own computer: it listens only on `localhost`, has no sign-in, and
runs `terraform` locally. Before it faces the internet, the points in
[`docs/cloud-publishing.md`](cloud-publishing.md) must be fixed (sign-in, AI quotas, a sandbox for `terraform
validate`, per-user queues, provider schemas built into the image). That document also has the target Azure
architecture and its monthly cost.
