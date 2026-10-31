import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html, css } from "@umbraco-cms/backoffice/external/lit";

const pathBase = new URL(import.meta.url).pathname.split("/App_Plugins/")[0];

export default class WelcomeDashboard extends UmbLitElement {
    static properties = { ready: { state: true }, error: { state: true } };
    static styles = css`
        :host { display: block; padding: var(--uui-size-layout-1); }
        h1 { margin: 0 0 var(--uui-size-space-3); }
        p { margin: 0 0 var(--uui-size-space-5); color: var(--uui-color-text-alt); }
        interactive-report { display: block; min-width: 0; }
        .error { color: var(--uui-color-danger); }
    `;

    constructor() {
        super();
        this.ready = false;
        this.error = false;
        this.loadReport();
    }

    async loadReport() {
        this.error = false;
        try {
            await import(`${pathBase}/umbraco/reports/ui/ir.js`);
            this.ready = true;
        } catch {
            this.error = true;
        }
    }

    render() {
        return html`
			<h1>Latest Changes</h1>
                ${this.ready ? html`
                    <interactive-report
                        report="umbraco-recent-pages"
                        api-base=${`${pathBase}/umbraco/reports`}
                        download-base=${`${pathBase}/umbraco/report-download`}
                        stylesheet=${`${pathBase}/App_Plugins/BackOfficeExperience/welcome-report.css`}
                        empty-message="No pages match this view."
                    ></interactive-report>
                ` : this.error ? html`
                    <p class="error" role="alert">The report could not be loaded.</p>
                    <uui-button look="secondary" label="Try again" @click=${this.loadReport}>Try again</uui-button>
                ` : html`<p role="status">Loading recently changed pages…</p>`}
        `;
    }
}

customElements.define("backoffice-welcome-dashboard", WelcomeDashboard);
