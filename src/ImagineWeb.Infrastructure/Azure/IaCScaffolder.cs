using System.Text;
using Microsoft.Extensions.Logging;

namespace ImagineWeb.Infrastructure.Azure;

public sealed class IaCScaffolder
{
    private readonly ILogger<IaCScaffolder> _logger;

    public IaCScaffolder(ILogger<IaCScaffolder> logger) => _logger = logger;

    public ScaffoldResult Scaffold(string projectRoot, DetectedResources resources)
    {
        var siteDir = Path.Combine(projectRoot, "site");
        var infraDir = Path.Combine(projectRoot, "infra");

        Directory.CreateDirectory(infraDir);

        var azureYaml = BuildAzureYaml(resources);
        var pipeline = BuildPipelineYaml();
        var mainBicep = BuildMainBicep(resources);
        var modulesBicep = BuildResourcesBicep(resources);
        var bicepParam = BuildBicepParam();

        File.WriteAllText(Path.Combine(projectRoot, "azure.yaml"), azureYaml);
        File.WriteAllText(Path.Combine(projectRoot, "azure-pipelines.yml"), pipeline);
        File.WriteAllText(Path.Combine(infraDir, "main.bicep"), mainBicep);
        File.WriteAllText(Path.Combine(infraDir, "resources.bicep"), modulesBicep);
        File.WriteAllText(Path.Combine(infraDir, "main.bicepparam"), bicepParam);

        _logger.LogInformation("IaC scaffolded for {Host} at {Path}", resources.PrimaryHost, projectRoot);

        return new ScaffoldResult
        {
            AzureYamlPath = Path.Combine(projectRoot, "azure.yaml"),
            MainBicepPath = Path.Combine(infraDir, "main.bicep"),
            ResourcesBicepPath = Path.Combine(infraDir, "resources.bicep"),
            BicepParamPath = Path.Combine(infraDir, "main.bicepparam"),
            PipelinePath = Path.Combine(projectRoot, "azure-pipelines.yml"),
            DetectedHost = resources.PrimaryHost
        };
    }

    private static string BuildAzureYaml(DetectedResources resources)
    {
        var sb = new StringBuilder();
        sb.AppendLine("name: web-app");
        sb.AppendLine("services:");
        sb.AppendLine("  web:");
        sb.AppendLine("    project: ./site");
        sb.AppendLine($"    host: {HostToAzd(resources.PrimaryHost)}");

        if (resources.AzdLanguage is not null)
            sb.AppendLine($"    language: {resources.AzdLanguage}");

        if (resources.PrimaryHost == AzureHostType.StaticWebApp)
            sb.AppendLine($"    dist: {resources.BuildOutputDir ?? "."}");

        if (resources.NeedsBuildStep && resources.BuildCommand is not null)
        {
            sb.AppendLine("    hooks:");
            sb.AppendLine("      prepackage:");
            sb.AppendLine("        shell: pwsh");
            sb.AppendLine($"        run: {resources.BuildCommand}");
        }

        return sb.ToString();
    }

    private static string BuildPipelineYaml() => """
        trigger:
          branches:
            include:
              - main

        pool:
          vmImage: ubuntu-latest

        steps:
          - task: UseNode@1
            inputs:
              version: '20.x'

          - script: |
              curl -fsSL https://aka.ms/install-azd.sh | bash
            displayName: Install azd

          - script: |
              azd auth login --client-id $(AZURE_CLIENT_ID) --client-secret $(AZURE_CLIENT_SECRET) --tenant-id $(AZURE_TENANT_ID)
              azd env new $(AZURE_ENV_NAME) --subscription $(AZURE_SUBSCRIPTION_ID) --location $(AZURE_LOCATION) --no-prompt
              azd provision --no-prompt
              azd deploy --no-prompt
            displayName: Deploy to Azure
            env:
              AZURE_CLIENT_ID: $(AZURE_CLIENT_ID)
              AZURE_CLIENT_SECRET: $(AZURE_CLIENT_SECRET)
              AZURE_TENANT_ID: $(AZURE_TENANT_ID)
              AZURE_SUBSCRIPTION_ID: $(AZURE_SUBSCRIPTION_ID)
              AZURE_ENV_NAME: $(AZURE_ENV_NAME)
              AZURE_LOCATION: $(AZURE_LOCATION)
        """;

    private static string BuildMainBicep(DetectedResources resources) => $$"""
        targetScope = 'subscription'

        @minLength(1)
        @maxLength(64)
        param environmentName string

        @minLength(1)
        param location string

        var tags = { 'azd-env-name': environmentName }

        resource rg 'Microsoft.Resources/resourceGroups@2021-04-01' = {
          name: 'rg-${environmentName}'
          location: location
          tags: tags
        }

        module resources 'resources.bicep' = {
          name: 'resources'
          scope: rg
          params: {
            location: location
            environmentName: environmentName
            tags: tags
          }
        }

        output AZURE_RESOURCE_GROUP string = rg.name
        output SERVICE_WEB_URL string = resources.outputs.serviceUrl
        """;

    private static string BuildResourcesBicep(DetectedResources resources) =>
        resources.PrimaryHost switch
        {
            AzureHostType.StaticWebApp => BuildStaticWebAppBicep(),
            AzureHostType.AppService => BuildAppServiceBicep(resources),
            AzureHostType.ContainerApp => BuildContainerAppBicep(),
            AzureHostType.FunctionApp => BuildFunctionAppBicep(resources),
            _ => BuildStaticWebAppBicep()
        };

    private static string BuildStaticWebAppBicep() => """
        param location string
        param environmentName string
        param tags object

        resource staticWebApp 'Microsoft.Web/staticSites@2022-09-01' = {
          name: 'swa-${environmentName}'
          location: location
          tags: union(tags, { 'azd-service-name': 'web' })
          sku: {
            name: 'Free'
            tier: 'Free'
          }
          properties: {}
        }

        output serviceUrl string = 'https://${staticWebApp.properties.defaultHostname}'
        """;

    private static string BuildAppServiceBicep(DetectedResources resources)
    {
        var dotnetVersion = resources.RuntimeVersion ?? "10.0";
        var runtimeStack = resources.Runtime switch
        {
            "dotnet" => $"DOTNETCORE|{dotnetVersion}",
            "node" => "NODE|20-lts",
            "python" => "PYTHON|3.12",
            _ => $"DOTNETCORE|{dotnetVersion}"
        };

        return $$"""
            param location string
            param environmentName string
            param tags object

            resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
              name: 'plan-${environmentName}'
              location: location
              tags: tags
              kind: 'linux'
              sku: {
                name: 'F1'
                tier: 'Free'
              }
              properties: {
                reserved: true
              }
            }

            resource webApp 'Microsoft.Web/sites@2023-12-01' = {
              name: 'app-${environmentName}'
              location: location
              tags: union(tags, { 'azd-service-name': 'web' })
              kind: 'app,linux'
              identity: {
                type: 'SystemAssigned'
              }
              properties: {
                serverFarmId: appServicePlan.id
                httpsOnly: true
                siteConfig: {
                  minTlsVersion: '1.2'
                  http20Enabled: true
                  linuxFxVersion: '{{runtimeStack}}'
                  alwaysOn: false
                  appSettings: [
                    { name: 'SCM_DO_BUILD_DURING_DEPLOYMENT', value: 'false' }
                    { name: 'ENABLE_ORYX_BUILD', value: 'false' }
                  ]
                }
              }
            }

            output serviceUrl string = 'https://${webApp.properties.defaultHostName}'
            """;
    }

    private static string BuildContainerAppBicep() => """
        param location string
        param environmentName string
        param tags object

        resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
          name: 'log-${environmentName}'
          location: location
          tags: tags
          properties: {
            sku: {
              name: 'PerGB2018'
            }
            retentionInDays: 30
          }
        }

        resource containerAppEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
          name: 'cae-${environmentName}'
          location: location
          tags: tags
          properties: {
            appLogsConfiguration: {
              destination: 'log-analytics'
              logAnalyticsConfiguration: {
                customerId: logAnalytics.properties.customerId
                sharedKey: logAnalytics.listKeys().primarySharedKey
              }
            }
          }
        }

        resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
          name: 'ca-${environmentName}'
          location: location
          tags: union(tags, { 'azd-service-name': 'web' })
          identity: {
            type: 'SystemAssigned'
          }
          properties: {
            managedEnvironmentId: containerAppEnv.id
            configuration: {
              ingress: {
                external: true
                targetPort: 8080
                transport: 'http'
              }
            }
            template: {
              containers: [
                {
                  name: 'main'
                  image: 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
                  resources: {
                    cpu: json('0.25')
                    memory: '0.5Gi'
                  }
                }
              ]
              scale: {
                minReplicas: 0
                maxReplicas: 1
              }
            }
          }
        }

        output serviceUrl string = 'https://${containerApp.properties.configuration.ingress.fqdn}'
        """;

    private static string BuildFunctionAppBicep(DetectedResources resources)
    {
        var workerRuntime = resources.Runtime switch
        {
            "dotnet" => "dotnet-isolated",
            "node" => "node",
            "python" => "python",
            _ => "node"
        };

        return $$"""
            param location string
            param environmentName string
            param tags object

            resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
              name: take('st${replace(environmentName, '-', '')}', 24)
              location: location
              tags: tags
              sku: {
                name: 'Standard_LRS'
              }
              kind: 'StorageV2'
              properties: {
                supportsHttpsTrafficOnly: true
                minimumTlsVersion: 'TLS1_2'
              }
            }

            resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
              name: 'plan-${environmentName}'
              location: location
              tags: tags
              sku: {
                name: 'Y1'
                tier: 'Dynamic'
              }
              properties: {
                reserved: true
              }
            }

            resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
              name: 'func-${environmentName}'
              location: location
              tags: union(tags, { 'azd-service-name': 'web' })
              kind: 'functionapp,linux'
              identity: {
                type: 'SystemAssigned'
              }
              properties: {
                serverFarmId: appServicePlan.id
                httpsOnly: true
                siteConfig: {
                  minTlsVersion: '1.2'
                  linuxFxVersion: ''
                  appSettings: [
                    { name: 'AzureWebJobsStorage', value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=core.windows.net;AccountKey=${storageAccount.listKeys().keys[0].value}' }
                    { name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING', value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=core.windows.net;AccountKey=${storageAccount.listKeys().keys[0].value}' }
                    { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
                    { name: 'FUNCTIONS_WORKER_RUNTIME', value: '{{workerRuntime}}' }
                  ]
                }
              }
            }

            output serviceUrl string = 'https://${functionApp.properties.defaultHostName}'
            """;
    }

    private static string BuildBicepParam() => """
        using './main.bicep'

        param environmentName = readEnvironmentVariable('AZURE_ENV_NAME', 'dev')
        param location = readEnvironmentVariable('AZURE_LOCATION', 'eastus2')
        """;

    private static string HostToAzd(AzureHostType host) => host switch
    {
        AzureHostType.StaticWebApp => "staticwebapp",
        AzureHostType.AppService => "appservice",
        AzureHostType.ContainerApp => "containerapp",
        AzureHostType.FunctionApp => "function",
        _ => "staticwebapp"
    };
}

public sealed class ScaffoldResult
{
    public required string AzureYamlPath { get; init; }
    public required string MainBicepPath { get; init; }
    public required string ResourcesBicepPath { get; init; }
    public required string BicepParamPath { get; init; }
    public required string PipelinePath { get; init; }
    public required AzureHostType DetectedHost { get; init; }
}
