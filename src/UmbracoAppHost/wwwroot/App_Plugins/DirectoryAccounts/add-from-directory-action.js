import { UmbCollectionActionBase, UMB_COLLECTION_CONTEXT } from "@umbraco-cms/backoffice/collection";
import { UmbModalToken, umbOpenModal } from "@umbraco-cms/backoffice/modal";
import { UMB_NOTIFICATION_CONTEXT } from "@umbraco-cms/backoffice/notification";

// The "Add from directory" button of the Users and Members collections. Registered twice in
// umbraco-package.json as a collectionAction of the "button" kind; meta.accountKind says which
// collection it sits in. It opens the dialog, and once an account is created reloads the collection
// so the new account shows, and says so with a notification.
export const DIRECTORY_ACCOUNTS_MODAL = new UmbModalToken("DirectoryAccounts.Modal.AddFromDirectory", {
    modal: { type: "sidebar", size: "medium" },
});

export class AddFromDirectoryCollectionAction extends UmbCollectionActionBase {
    async execute() {
        const kind = this.manifest?.meta?.accountKind === "member" ? "member" : "user";
        let value;
        try {
            value = await umbOpenModal(this, DIRECTORY_ACCOUNTS_MODAL, { data: { kind } });
        } catch {
            // Closed without creating anything.
            return;
        }
        const created = value?.created;
        if (!created) return;

        try {
            const collection = await this.getContext(UMB_COLLECTION_CONTEXT);
            collection?.loadCollection();
        } catch {
            // The collection is gone; the account shows the next time it opens.
        }
        try {
            const notifications = await this.getContext(UMB_NOTIFICATION_CONTEXT);
            notifications?.peek("positive", {
                data: {
                    headline: kind === "member" ? "Member created" : "User created",
                    message: `${created.name} (${created.username}) can sign in with their Windows account.`,
                },
            });
        } catch {
            // No notifications here; the collection already shows the account.
        }
    }
}

export { AddFromDirectoryCollectionAction as api };
