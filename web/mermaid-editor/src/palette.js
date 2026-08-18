import { t } from "../../shared/i18n/index.js";

export { PALETTE, PALETTE_BY_ID } from "@markup-view-mini/mermaid-safe/palette";

// "default" is not a colour - it means emit no style syntax at all - so it is named as a state.
export function paletteLabel(id) {
  return t(`mermaid.color.${id}`);
}
