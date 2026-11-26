import { element } from "../admin-catalog-form.js";
import { startAdminSession } from "../admin-session.js";
import { operationDialog } from "../admin-stock-ui.js";
import { positiveInt } from "../catalog.js";
import { showInventory } from "./admin-inventory.js";
import { showProductAssets } from "./admin-product-assets.js";

const workspace = document.querySelector("[data-workspace]");
const query = new URLSearchParams(location.search);
const inventory = query.get("view") === "inventory";
const productId = positiveInt(query.get("productId"));
const pageTitle = inventory ? "Tồn kho" : "Biến thể và ảnh";
document.title = pageTitle + " — Quản trị SportShop";
document.querySelector("[data-page-title]").textContent = pageTitle;
document.querySelector(inventory ? "[data-inventory-nav]" : "[data-product-nav]").setAttribute("aria-current", "page");
const notice = element("p", null, { role: "status", "data-feedback": "", hidden: "" });
function feedback(message, warning = false) {
  notice.textContent = message;
  notice.hidden = !message;
  notice.toggleAttribute("data-warning", warning);
}
const dialog = operationDialog(feedback);
let session;

startAdminSession({
  account: new URL("../../../account/", location.href),
  view: inventory ? "inventory" : "product",
  productId,
  clear() {
    session?.abort();
    session = null;
    dialog.dismiss();
    feedback("");
    workspace.replaceChildren();
  },
  load() {
    session = new AbortController();
    workspace.append(notice);
    const context = { workspace, dialog, session: session.signal };
    if (inventory) showInventory(context, query.get("keyword") ?? "");
    else if (productId) showProductAssets(context, productId);
    else {
      const panel = element("section", null, { class: "admin-panel" });
      panel.append(element("h2", "Chọn sản phẩm"),
        element("a", "Mở danh sách sản phẩm", { href: "../", class: "button button-outline" }));
      workspace.append(panel);
    }
  }
});
