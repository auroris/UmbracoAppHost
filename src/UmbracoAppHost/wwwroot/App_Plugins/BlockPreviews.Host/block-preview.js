import { BlockPreview } from "../ApplicationBlocks/block-preview.js";
import { heading, richText } from "../ApplicationBlocks/preview-model.js";
export default class DemoBlockPreview extends BlockPreview {
    renderContent(alias, content) {
        switch (alias) {
            case "umbBlockGridDemoHeadlineBlock": return heading(content.headline);
            case "umbBlockGridDemoRichTextBlock": return richText(content.richText);
            case "umbBlockGridDemoImageBlock": return this.mediaImage(content.image);
            default: return super.renderContent(alias, content);
        }
    }
}
customElements.define("app-host-demo-preview", DemoBlockPreview);
