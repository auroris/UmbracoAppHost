import { UmbModalBaseElement } from "@umbraco-cms/backoffice/modal";
import { html, css, nothing, repeat } from "@umbraco-cms/backoffice/external/lit";
import { umbFocus } from "@umbraco-cms/backoffice/lit-element";
import { UmbTextStyles } from "@umbraco-cms/backoffice/style";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";

// The "Add from directory" dialog: search the directory, choose a person, choose their groups, and
// create a backoffice user or a member that is linked to their Windows identity from the start.
// The site's management API does the work (DirectoryAccountsController); the dialog sends only the
// person's SID and the choices, and the server reads everything else from the directory again.
const pathBase = new URL(import.meta.url).pathname.split("/App_Plugins/")[0];
const managementApi = `${pathBase}/umbraco/management/api/v1`;
const minimumLength = 2;
const searchDelay = 300;
const take = 20;

const words = {
    user: { noun: "user", headline: "Add user from directory", create: "Create user", section: "Users" },
    member: { noun: "member", headline: "Add member from directory", create: "Create member", section: "Members" },
};

const states = {
    active: { label: "Has an account", color: "default" },
    disabled: { label: "Account disabled", color: "warning" },
    retired: { label: "Account retired", color: "danger" },
};

class RequestError extends Error {}

export default class DirectoryAccountsModalElement extends UmbModalBaseElement {
    static properties = {
        _query: { state: true },
        _searching: { state: true },
        _searchError: { state: true },
        _results: { state: true },
        _existing: { state: true },
        _selected: { state: true },
        _selectedAccount: { state: true },
        _userGroups: { state: true },
        _memberTypes: { state: true },
        _memberGroups: { state: true },
        _optionsError: { state: true },
        _groups: { state: true },
        _memberType: { state: true },
        _createState: { state: true },
        _createError: { state: true },
    };

    static styles = [UmbTextStyles, css`
        uui-box + uui-box { margin-top: var(--uui-size-space-5); }
        #search { width: 100%; }
        #search-icon { display: flex; height: 100%; padding-left: var(--uui-size-space-2); color: var(--uui-color-border-emphasis); }
        uui-loader-bar { margin-top: var(--uui-size-space-3); }
        .hint { margin: var(--uui-size-space-3) 0 0; color: var(--uui-color-text-alt); }
        .error { margin: var(--uui-size-space-3) 0 0; color: var(--uui-color-danger); }
        .results { margin-top: var(--uui-size-space-4); }
        dl { display: grid; grid-template-columns: max-content 1fr; gap: var(--uui-size-space-2) var(--uui-size-space-5); margin: 0; }
        dt { font-weight: bold; }
        dd { margin: 0; overflow-wrap: anywhere; }
        .notice { margin: var(--uui-size-space-4) 0 0; }
        .choices { display: flex; flex-direction: column; gap: var(--uui-size-space-3); }
        uui-select { width: 100%; }
    `];

    #auth;
    #timer;
    #search;

    constructor() {
        super();
        this._query = "";
        this._searching = false;
        this._results = [];
        this._existing = {};
        this._userGroups = [];
        this._memberTypes = [];
        this._memberGroups = [];
        this._groups = new Set();
        this._memberType = "";
    }

    firstUpdated() {
        // The modal has its data by its first render, so the kind of account is known here.
        this.#loadChoices();
    }

    disconnectedCallback() {
        super.disconnectedCallback();
        clearTimeout(this.#timer);
        this.#search?.abort();
    }

    get #kind() {
        return this.data?.kind === "member" ? "member" : "user";
    }

    get #words() {
        return words[this.#kind];
    }

    get #canCreate() {
        if (!this._selected || this._selectedAccount || this._createState === "waiting") return false;
        return this.#kind === "user" ? this._groups.size > 0 : Boolean(this._memberType);
    }

    // Calls this site's management API with the backoffice's token, as the other site extensions do,
    // and turns a refusal into the message the server wrote for it.
    async #request(path, { method = "GET", body, signal } = {}) {
        this.#auth ??= await this.getContext(UMB_AUTH_CONTEXT);
        const token = await this.#auth?.getLatestToken();
        const headers = { Accept: "application/json" };
        if (body !== undefined) headers["Content-Type"] = "application/json";
        if (token) headers.Authorization = `Bearer ${token}`;
        let response;
        try {
            response = await fetch(`${managementApi}/${path}`, {
                method, signal, credentials: "same-origin", headers,
                body: body === undefined ? undefined : JSON.stringify(body),
            });
        } catch (error) {
            if (error?.name === "AbortError") throw error;
            throw new RequestError("The site could not be reached. Check the connection and try again.");
        }
        const text = await response.text();
        let content = null;
        try { content = text ? JSON.parse(text) : null; } catch { content = null; }
        if (response.ok) return content;
        if (response.status === 401) throw new RequestError("Your session has ended. Sign in again, then try again.");
        if (response.status === 403 && !content?.detail) throw new RequestError("You do not have access to do this.");
        throw new RequestError(content?.detail || content?.title || `The request failed (${response.status}).`);
    }

    async #loadChoices() {
        this._optionsError = undefined;
        try {
            if (this.#kind === "user") {
                const groups = await this.#request("user-group?skip=0&take=1000");
                this._userGroups = sortByName(groups?.items ?? []);
                return;
            }
            const [allowed, memberGroups] = await Promise.all([
                this.#request("member-type/allowed-at-root?skip=0&take=100"),
                this.#request("member-group?skip=0&take=1000"),
            ]);
            this._memberGroups = sortByName(memberGroups?.items ?? []);
            const ids = (allowed?.items ?? []).map((type) => type.id);
            if (ids.length === 0) {
                this._optionsError = "There is no member type to create members of. Create one in Settings first.";
                return;
            }
            // The allowed types carry no alias, which is what the server takes.
            const batch = await this.#request(`member-type/batch?${ids.map((id) => `id=${encodeURIComponent(id)}`).join("&")}`);
            this._memberTypes = sortByName((batch?.items ?? []).map((type) => ({ alias: type.alias, name: type.name })));
            this._memberType = this._memberTypes.length === 1 ? this._memberTypes[0].alias : "";
        } catch (error) {
            this._optionsError = `The ${this.#kind === "user" ? "user groups" : "member types and groups"} could not be loaded. ${error.message}`;
        }
    }

    #onInput(event) {
        this._query = event.target.value ?? "";
        clearTimeout(this.#timer);
        const query = this._query.trim();
        if (query.length < minimumLength) {
            this.#search?.abort();
            this._searching = false;
            this._searchError = undefined;
            this._results = [];
            this._existing = {};
            return;
        }
        this.#timer = setTimeout(() => this.#runSearch(query), searchDelay);
    }

    async #runSearch(query) {
        this.#search?.abort();
        const search = (this.#search = new AbortController());
        this._searching = true;
        this._searchError = undefined;
        try {
            const found = await this.#request(
                `directory/${this.#kind}s/search?query=${encodeURIComponent(query)}&take=${take}`, { signal: search.signal });
            if (search.signal.aborted) return;
            this._results = found?.results ?? [];
            this._existing = found?.existing ?? {};
        } catch (error) {
            if (search.signal.aborted) return;
            this._results = [];
            this._existing = {};
            this._searchError = error.message;
        } finally {
            if (!search.signal.aborted) this._searching = false;
        }
    }

    // The chosen person stays chosen while the search changes, with the account the search said
    // they had; the server checks again when the account is created.
    #choose(person) {
        this._selected = person;
        this._selectedAccount = person ? this._existing[person.sid] : undefined;
        this._createState = undefined;
        this._createError = undefined;
    }

    #toggleGroup(value, checked) {
        const groups = new Set(this._groups);
        if (checked) groups.add(value);
        else groups.delete(value);
        this._groups = groups;
    }

    async #create() {
        const person = this._selected;
        if (!this.#canCreate) return;
        this._createState = "waiting";
        this._createError = undefined;
        const body = this.#kind === "user"
            ? { sid: person.sid, userGroupKeys: [...this._groups] }
            : { sid: person.sid, memberTypeAlias: this._memberType || null, memberGroupNames: [...this._groups] };
        try {
            const created = await this.#request(`directory/${this.#kind}s`, { method: "POST", body });
            this._createState = "success";
            this.updateValue({ created });
            this._submitModal();
        } catch (error) {
            this._createState = "failed";
            this._createError = error.message;
        }
    }

    render() {
        return html`
            <umb-body-layout headline=${this.#words.headline}>
                <uui-box headline="Find the person">
                    <uui-input
                        id="search"
                        type="search"
                        label="Search the directory"
                        placeholder="Name, username or email"
                        .value=${this._query}
                        @input=${this.#onInput}
                        ${umbFocus()}>
                        <uui-icon name="search" slot="prepend" id="search-icon"></uui-icon>
                    </uui-input>
                    ${this._searching ? html`<uui-loader-bar></uui-loader-bar>` : nothing}
                    ${this.#renderResults()}
                </uui-box>
                ${this._selected ? this.#renderPerson(this._selected) : nothing}
                ${this._selected && !this._selectedAccount ? this.#renderChoices() : nothing}
                ${this._createError ? html`<p class="error" role="alert">${this._createError}</p>` : nothing}
                <div slot="actions">
                    <uui-button label="Close" @click=${this._rejectModal}></uui-button>
                    <uui-button
                        label=${this.#words.create}
                        look="primary"
                        color="positive"
                        .state=${this._createState === "waiting" ? "waiting" : undefined}
                        ?disabled=${!this.#canCreate}
                        @click=${this.#create}></uui-button>
                </div>
            </umb-body-layout>
        `;
    }

    #renderResults() {
        if (this._searchError) return html`<p class="error" role="alert">${this._searchError}</p>`;
        const query = this._query.trim();
        if (query.length < minimumLength)
            return html`<p class="hint">Type at least ${minimumLength} characters of the person's name, username or email.</p>`;
        if (this._searching && this._results.length === 0) return nothing;
        if (this._results.length === 0)
            return html`<p class="hint" role="status">Nobody in the directory matches “${query}”.</p>`;
        return html`
            <uui-ref-list class="results">
                ${repeat(this._results, (person) => person.sid, (person) => this.#renderResult(person))}
            </uui-ref-list>
            ${this._results.length >= take
                ? html`<p class="hint">Showing the first ${take} people. Add to the search to narrow it down.</p>`
                : nothing}
        `;
    }

    #renderResult(person) {
        const account = this._existing[person.sid];
        const detail = [person.accountName, person.email, person.title || person.department].filter(Boolean).join(" · ");
        const tag = account ? html`<uui-tag slot="tag" look="secondary" color=${states[account.state]?.color ?? "default"}>${states[account.state]?.label ?? "Has an account"}</uui-tag>` : nothing;
        const selected = this._selected?.sid === person.sid;
        return this.#kind === "user"
            ? html`<uui-ref-node-user name=${person.displayName} detail=${detail} selectable select-only ?selected=${selected}
                    @selected=${() => this.#choose(person)} @deselected=${() => this.#choose(undefined)}>${tag}</uui-ref-node-user>`
            : html`<uui-ref-node-member name=${person.displayName} detail=${detail} selectable select-only ?selected=${selected}
                    @selected=${() => this.#choose(person)} @deselected=${() => this.#choose(undefined)}>${tag}</uui-ref-node-member>`;
    }

    #renderPerson(person) {
        const username = person.userName.toLowerCase();
        const account = this._selectedAccount;
        return html`
            <uui-box headline=${person.displayName}>
                <dl>
                    <dt>Windows account</dt><dd>${person.accountName}</dd>
                    <dt>Username</dt><dd>${username}</dd>
                    <dt>Email</dt><dd>${person.email ?? html`${username}@localhost <span class="hint">(the directory has none)</span>`}</dd>
                    ${person.title ? html`<dt>Title</dt><dd>${person.title}</dd>` : nothing}
                    ${person.department ? html`<dt>Department</dt><dd>${person.department}</dd>` : nothing}
                </dl>
                ${account ? html`<p class="notice" role="status">${this.#describeAccount(person, account)}</p>` : nothing}
            </uui-box>
        `;
    }

    #describeAccount(person, account) {
        const { noun, section } = this.#words;
        if (account.state === "retired")
            return `${person.displayName}'s Windows identity belongs to a retired ${noun}, so another cannot be created for it. "Retiring accounts" in the site's README says how to bring the person back.`;
        if (account.state === "disabled")
            return `${person.displayName} already has the ${noun} ${account.username}, which is disabled. Enable it in ${section} instead of creating another.`;
        return `${person.displayName} already has the ${noun} ${account.username}, linked to their Windows identity.`;
    }

    #renderChoices() {
        if (this._optionsError) return html`<uui-box><p class="error" role="alert">${this._optionsError}</p></uui-box>`;
        return this.#kind === "user" ? this.#renderUserGroups() : this.#renderMemberChoices();
    }

    #renderUserGroups() {
        return html`
            <uui-box headline="User groups">
                <p class="hint">Choose the groups that give this person the access they need; at least one.</p>
                <div class="choices">
                    ${repeat(this._userGroups, (group) => group.id, (group) => html`
                        <uui-checkbox
                            label=${group.name}
                            .checked=${this._groups.has(group.id)}
                            @change=${(event) => this.#toggleGroup(group.id, event.target.checked)}></uui-checkbox>`)}
                </div>
            </uui-box>
        `;
    }

    #renderMemberChoices() {
        return html`
            ${this._memberTypes.length > 1 ? html`
                <uui-box headline="Member type">
                    <uui-select
                        label="Member type"
                        placeholder="Choose a member type"
                        .options=${this._memberTypes.map((type) => ({ name: type.name, value: type.alias, selected: type.alias === this._memberType }))}
                        @change=${(event) => { this._memberType = event.target.value; }}></uui-select>
                </uui-box>` : nothing}
            <uui-box headline="Member groups">
                ${this._memberGroups.length === 0
                    ? html`<p class="hint">There are no member groups yet.</p>`
                    : html`
                        <div class="choices">
                            ${repeat(this._memberGroups, (group) => group.id, (group) => html`
                                <uui-checkbox
                                    label=${group.name}
                                    .checked=${this._groups.has(group.name)}
                                    @change=${(event) => this.#toggleGroup(group.name, event.target.checked)}></uui-checkbox>`)}
                        </div>`}
            </uui-box>
        `;
    }
}

function sortByName(items) {
    return [...items].sort((a, b) => (a.name ?? "").localeCompare(b.name ?? "", undefined, { sensitivity: "base" }));
}

customElements.define("directory-accounts-modal", DirectoryAccountsModalElement);
