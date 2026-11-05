import { previewStylesheets } from "../ApplicationBlocks/block-preview.js";
const pathBase = new URL(import.meta.url).pathname.split("/App_Plugins/")[0];
export function onInit() {
    // A consuming theme can replace this slot with its own existing frontend bundle.
    previewStylesheets[1] ??= `${pathBase}/dist/css/site.css`;
}
