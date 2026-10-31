# Test fixtures

`spfx-probe.sppkg` and `probe/` are a real SharePoint Framework **1.24.0** project and the package
`heft package-solution --production` built from it on 2026-10-31 (`@rushstack/heft` 1.3.2,
`@microsoft/spfx-web-build-rig` 1.24.0). The project was scaffolded with `@microsoft/spfx-cli`
0.1.0-pre.3 and then changed so that one package exercises every inspector path:

| What | Where it shows up in the package |
|---|---|
| React web part, `requiresCustomScript: true` | `WebPart_cc9029b5….xml`, `ReturnIfCustomScriptDisabled="false"` |
| jQuery as a CDN external (`config/config.json`) | the web part's `loaderConfig.scriptResources.jquery.path` |
| Two `webApiPermissionRequests` (Microsoft Graph `Sites.Read.All`, Contoso API `user_impersonation`) | `AppManifest.xml` |
| Application customizer extension | `Extension_d31829d6….xml` |
| Its `CustomAction` and tenant-wide `ClientSideComponentInstance` | `elements.xml`, `ClientSideInstance.xml` |
| `skipFeatureDeployment: true`, solution version `1.2.3.4` | `AppManifest.xml` |

`probe/` holds only the files `SpfxProject.Load` reads (`package.json`, `config/*.json`), not the sources.
