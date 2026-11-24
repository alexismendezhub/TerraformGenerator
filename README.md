# Terraform Generator

**Generate Infrastructure as Code (Terraform and Bicep) the easy way.** Terraform Generator writes the Terraform (and
Bicep, for Azure) that creates your cloud infrastructure on Azure, AWS or Google Cloud, so you do not have to write it
by hand or know Terraform. You also get the deploy scripts and, if you want, a GitHub pipeline.

Infrastructure as Code (IaC) means the infrastructure is described in files that create it the same way every time.

## How to use it

You can tell it what to build in three ways:

- **Draw it:** pick boxes, connect them, and choose their options from lists.
- **Describe it:** write what you need in your own words and Claude (AI) proposes the drawing.
- **Import it:** open the `.tf` files of an environment you already have.

The **Code** tab shows every file as you draw, and **Check the code** runs `terraform validate`,
`terraform fmt -check` and the Bicep compiler on your computer, without creating anything in the cloud. **Download**
gives a ZIP with the code, the deploy scripts, a step-by-step README and `sketch.json` (your drawing).

## How to run it

**Requirements:** the [.NET 10 SDK](https://dotnet.microsoft.com/download), [Node.js](https://nodejs.org) 20, 22 or 24,
and PowerShell 7 (`pwsh`). No database is needed.

```cmd
scripts\start.cmd
```

It builds the screen, starts the app at http://localhost:5080 and opens your browser. The app listens only on your
computer. You can also open `TerraformGenerator.slnx` in Visual Studio and start **TerraformGenerator.Server**.

## Configuration

It needs no configuration to run. Two things are optional:

- **The AI.** Describing, the AI review and import with AI use Claude. Set your API key before you start the app and
  open a new terminal afterwards. Without a key the drawing works the same and the AI buttons are disabled.

  ```powershell
  setx ANTHROPIC_API_KEY "your-key"
  ```

- **Checking the code from the screen.** It needs [Terraform](https://developer.hashicorp.com/terraform/install)
  (`winget install Hashicorp.Terraform`) and Bicep (`az bicep install`).

## Tests

```cmd
scripts\build-and-test.cmd
```

It runs `npm audit`, the screen build, the .NET build (warnings as errors) and the .NET tests.

## License and disclaimer

Licensed under the [Apache License 2.0](LICENSE): anyone may use, modify and distribute it, including commercially.

It is provided as an example, "as is", without warranty of any kind. Review and test it before using it in
production; the authors are not liable for any damage that results from its use.
