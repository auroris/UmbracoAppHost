// Backoffice setup: hides the "Welcome to Umbraco" dashboard.
// Registered as a backofficeEntryPoint in umbraco-package.json, so it runs once after sign-in, before
// the sections render. The local Welcome dashboard is registered separately with a higher weight
// than Redirect URL Management, making it the first page in the Content section.
export const onInit = (host, extensionRegistry) => {
    extensionRegistry.exclude('Umb.Dashboard.UmbracoNews');
};
