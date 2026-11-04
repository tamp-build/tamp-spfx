// Regenerates vectors.json: debug-page URLs computed by the SPFx rig itself, which SpfxDebugUrlTests compare
// Tamp.Spfx's builder against. Run from any SPFx 1.22+ project with node_modules installed:
//
//   node generate.cjs <spfx project dir> > vectors.json
//
// It mirrors what `heft start` does: ConfigureServe.generateServeConfig() (hostname from ipAddress, port 4321)
// then SPFxDebugPageUrlUtilities.getServeInitialPageFromConfiguration(config, name).
'use strict';
const path = require('path');
const project = path.resolve(process.argv[2] || '.');
const plugins = path.dirname(require.resolve('@microsoft/spfx-heft-plugins/package.json', { paths: [project] }));
const { SPFxDebugPageUrlUtilities } = require(path.join(plugins, 'lib-commonjs/plugins/webpackConfigurationPlugin/SPFxDebugPageUrlUtilities.js'));
const rigVersion = require(path.join(plugins, 'package.json')).version;

const quiet = { writeLine() {}, writeVerboseLine() {}, writeWarningLine() {}, writeWarning() {} };

// The rig's ConfigureServe.generateServeConfig, minus the api map.
function generateServeConfig(serve) {
  return { hostname: serve.ipAddress ?? 'localhost', port: 4321, ...serve };
}

function compute(serve, configuration, tenantDomain) {
  if (tenantDomain === null) delete process.env.SPFX_SERVE_TENANT_DOMAIN;
  else process.env.SPFX_SERVE_TENANT_DOMAIN = tenantDomain;
  SPFxDebugPageUrlUtilities._domainEnvironmentVariableWarningLogged = true;
  const config = generateServeConfig(JSON.parse(JSON.stringify(serve)));
  const url = new SPFxDebugPageUrlUtilities(quiet).getServeInitialPageFromConfiguration(config, configuration ?? undefined);
  return url?.href ?? null;
}

const formCustomizer = (pageType, extra) => ({
  pageUrl: 'https://{tenantDomain}/_layouts/15/SPListForm.aspx',
  formCustomizer: { componentId: '2875713c-2c49-4baf-9725-fcc44d531e58', PageType: pageType, RootFolder: '/sites/mySite/Lists/MyList', ...extra, properties: { sampleText: 'Value' } },
});
const generated = {
  port: 4321, https: true,
  serveConfigurations: {
    default: formCustomizer(8),
    fixtureForm_NewForm: formCustomizer(8),
    fixtureForm_EditForm: formCustomizer(6, { ID: 1 }),
    fixtureForm_ViewForm: formCustomizer(4, { ID: 1 }),
  },
};

const cases = [
  ['form customizer: default', generated, null, 'contoso.sharepoint.com'],
  ['form customizer: new form', generated, 'fixtureForm_NewForm', 'contoso.sharepoint.com'],
  ['form customizer: edit form', generated, 'fixtureForm_EditForm', 'contoso.sharepoint.com'],
  ['form customizer: view form', generated, 'fixtureForm_ViewForm', 'contoso.sharepoint.com'],
  ['tenant domain missing', generated, 'fixtureForm_NewForm', null],
  ['web part workbench', { port: 4321, https: true, initialPage: 'https://{tenantDomain}/_layouts/workbench.aspx', serveConfigurations: { default: { pageUrl: 'https://{tenantDomain}/_layouts/workbench.aspx' } } }, null, 'contoso.sharepoint.com'],
  ['initialPage only, no serveConfigurations', { port: 4321, https: true, initialPage: 'https://contoso.sharepoint.com/_layouts/workbench.aspx' }, null, null],
  ['application customizer', { https: true, serveConfigurations: { default: { pageUrl: 'https://{tenantDomain}/sites/dev', customActions: { 'a1b2c3d4-0000-4000-8000-000000000001': { location: 'ClientSideExtension.ApplicationCustomizer', properties: { testMessage: 'Test message', count: 3 } } } } } }, null, 'contoso.sharepoint.com'],
  ['field customizers', { https: true, serveConfigurations: { default: { pageUrl: 'https://{tenantDomain}/sites/dev/Lists/Tasks/AllItems.aspx', fieldCustomizers: { Percent: { id: 'a1b2c3d4-0000-4000-8000-000000000002', properties: { sampleText: 'Value' } } } } } }, null, 'contoso.sharepoint.com'],
  ['command set and app customizer together', { https: true, serveConfigurations: { default: { pageUrl: 'https://{tenantDomain}/sites/dev/Lists/Tasks/AllItems.aspx', customActions: { 'a1b2c3d4-0000-4000-8000-000000000003': { location: 'ClientSideExtension.ListViewCommandSet.CommandBar', properties: { sampleTextOne: 'One item is selected in the list', sampleTextTwo: 'This command is always visible.' } }, 'a1b2c3d4-0000-4000-8000-000000000004': { location: 'ClientSideExtension.ApplicationCustomizer' } } } } }, null, 'contoso.sharepoint.com'],
  ['form customizer: encoded root folder, ID 0, special characters', { https: true, serveConfigurations: { default: { pageUrl: 'https://{tenantDomain}/_layouts/15/SPListForm.aspx', formCustomizer: { componentId: '2875713c-2c49-4baf-9725-fcc44d531e58', PageType: 6, RootFolder: '%2Fsites%2FmySite%2FLists%2FMy%20List', ID: 0, ContentTypeID: '0x0100ABC', properties: { text: 'a b&c=d/é ü "q" <x> ~*', nested: { n: 1.5, t: true, z: null, list: [1, 'two'] } } } } } }, null, 'contoso.sharepoint.com'],
  ['page url with query, fragment and upper-case host', { https: true, serveConfigurations: { default: { pageUrl: 'https://Contoso.SharePoint.com/sites/Dev/SitePages/Home.aspx?foo=1&bar=2#top' } } }, null, null],
  ['custom hostname, http, port', { port: 4500, https: false, hostname: 'dev.localhost', serveConfigurations: { default: { pageUrl: 'https://contoso.sharepoint.com/_layouts/workbench.aspx' } } }, null, null],
  ['ipAddress wildcard, no hostname', { port: 4321, https: true, ipAddress: '0.0.0.0', serveConfigurations: { default: { pageUrl: 'https://contoso.sharepoint.com/_layouts/workbench.aspx' } } }, null, null],
  ['tenant placeholder in the middle of the url, mixed case', { https: true, serveConfigurations: { default: { pageUrl: 'https://{TENANTDOMAIN}/sites/{tenantDomain}/x.aspx' } } }, null, 'contoso.sharepoint.com'],
  ['tenant domain on a GCC High host', { https: true, serveConfigurations: { default: { pageUrl: 'https://{tenantDomain}/_layouts/workbench.aspx' } } }, null, 'contoso.sharepoint.us'],
];

const vectors = cases.map(([name, serve, configuration, tenantDomain]) => ({
  name, serve, configuration, tenantDomain, expected: compute(serve, configuration, tenantDomain),
}));
process.stdout.write(JSON.stringify({ generatedBy: `@microsoft/spfx-heft-plugins ${rigVersion}`, vectors }, null, 2) + '\n');
