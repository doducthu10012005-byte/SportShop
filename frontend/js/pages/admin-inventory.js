import { field, element } from "../admin-catalog-form.js";
import { button, pagination, readPanel, table, tableRow } from "../admin-stock-ui.js";
import { authorizedRequest } from "../auth.js";
import { orderTime } from "../orders.js";

const transactionLabels = {
  Import: "Nhập kho", Adjustment: "Kiểm kê", Sale: "Bán hàng", OrderRestock: "Hoàn kho"
};
const number = new Intl.NumberFormat("vi-VN");

export function showInventory({ workspace, dialog, session }, initialKeyword) {
  const panel = element("section", null, { class: "admin-panel catalog-management", "aria-label": "Tồn kho theo biến thể" });
  panel.append(element("h2", "Tồn kho theo biến thể"));
  const form = element("form", null, { class: "management-filters stock-filters management-fields", "data-stock-filters": "" });
  const keyword = field(form, "keyword", "Tìm tên sản phẩm hoặc SKU", { type: "search", value: initialKeyword });
  field(form, "sort", "Sắp xếp", { options: [["variantId", "Mặc định"], ["productName", "Tên sản phẩm"], ["sku", "SKU"],
    ["quantityOnHand", "Tồn kho tăng dần"], ["-quantityOnHand", "Tồn kho giảm dần"]], value: "variantId" });
  const toggles = element("div", null, { class: "stock-filter-options" });
  field(toggles, "lowStock", "Sắp hết", { type: "checkbox", value: false });
  field(toggles, "outOfStock", "Hết hàng", { type: "checkbox", value: false });
  form.append(toggles, button("Lọc tồn kho", { type: "submit" }));
  panel.append(form);
  const stockGrid = table("Danh sách tồn kho", ["SKU", "Sản phẩm", "Size / Màu", "Tồn thực tế", "Ngưỡng thấp", "Thao tác"]);
  const stockOutput = element("div", null, { "data-inventory-output": "" });
  let listQuery = { page: 1, pageSize: 20, sort: "variantId", keyword: initialKeyword.trim() || null };
  const stockPages = pagination(page => { listQuery.page = page; void loadStock(); });
  stockOutput.append(stockGrid.region, stockPages.root);

  const historyPanel = element("section", null, { class: "admin-panel catalog-management", "aria-labelledby": "stock-history-title", hidden: "" });
  const historyHeading = element("div", null, { class: "admin-panel-heading" });
  const historyTitle = element("h2", "Lịch sử kho", { id: "stock-history-title" });
  const historyClose = button("Đóng lịch sử", { "data-close-history": "" });
  historyHeading.append(historyTitle, historyClose);
  historyPanel.append(historyHeading);
  const historyGrid = table("Lịch sử giao dịch kho", ["Thời điểm", "Loại", "Thay đổi", "Sau giao dịch", "Dòng đơn", "Người thực hiện", "Ghi chú"]);
  const historyOutput = element("div", null, { "data-history-output": "" });
  let historyRow;
  let historyPage = 1;
  const historyPages = pagination(page => { historyPage = page; void loadHistory(); });
  historyOutput.append(historyGrid.region, historyPages.root);
  const loadHistory = readPanel(historyPanel, historyOutput, session,
    async signal => (await authorizedRequest(`/admin/inventory/${historyRow.variantId}/transactions`, {
      query: { page: historyPage, pageSize: 20 }, signal
    })).data,
    ({ items, pagination: meta }) => {
      historyGrid.body.replaceChildren();
      if (!items.length) return false;
      historyPages.update(meta);
      for (const t of items) {
        const change = t.quantityChange > 0 ? "+" + number.format(t.quantityChange) : number.format(t.quantityChange);
        tableRow(historyGrid.body, orderTime(t.createdAt), transactionLabels[t.transactionType] ?? t.transactionType, change,
          number.format(t.quantityAfter), t.orderItemId, t.createdBy ?? "Hệ thống", t.note);
      }
    }, "Chưa có giao dịch kho");

  function history(row, focus = false) {
    historyRow = row;
    historyPage = 1;
    historyTitle.textContent = "Lịch sử kho · " + row.sku;
    historyPanel.hidden = false;
    void loadHistory();
    if (focus) {
      historyTitle.tabIndex = -1;
      historyTitle.focus();
    }
  }
  historyClose.addEventListener("click", () => {
    historyPanel.hidden = true;
    keyword.focus();
  });

  const loadStock = readPanel(panel, stockOutput, session,
    async signal => (await authorizedRequest("/admin/inventory", { query: listQuery, signal })).data,
    ({ items, pagination: meta }) => {
      stockGrid.body.replaceChildren();
      if (meta.page > 1 && meta.page > meta.totalPages) {
        listQuery.page = Math.max(1, meta.totalPages);
        void loadStock();
        return false;
      }
      if (!items.length) return false;
      stockPages.update(meta);
      for (const row of items) {
        const actions = element("div", null, { class: "management-actions" });
        const adjust = button("Cập nhật kho", { "data-adjust": "", "aria-label": "Cập nhật kho " + row.sku });
        const transactions = button("Lịch sử", { "data-history": "", "aria-label": "Lịch sử kho " + row.sku });
        adjust.addEventListener("click", () => openAdjustment(row));
        transactions.addEventListener("click", () => history(row, true));
        actions.append(adjust, transactions);
        const quantity = element("span", number.format(row.quantityOnHand), { "data-quantity": "" });
        if (row.quantityOnHand === 0) quantity.append(element("small", "Hết hàng"));
        else if (row.isLowStock) quantity.append(element("small", "Sắp hết"));
        const tr = tableRow(stockGrid.body, row.sku, row.productName, row.sizeName + " / " + row.colorName,
          quantity, number.format(row.lowStockThreshold), actions);
        tr.dataset.variantId = row.variantId;
      }
    }, "Không tìm thấy tồn kho");

  function openAdjustment(row) {
    void dialog.open(session, {
      title: "Cập nhật kho · " + row.sku, method: "POST", path: `/admin/inventory/${row.variantId}/adjustments`,
      label: "Ghi giao dịch", success: "Đã ghi giao dịch kho.",
      reload(uncertain) {
        void loadStock();
        if (uncertain || (!historyPanel.hidden && historyRow?.variantId === row.variantId)) history(row);
      },
      render(fields) {
        const hint = element("p", "Tồn kho khi tải dữ liệu: " + number.format(row.quantityOnHand), { class: "management-wide" });
        fields.append(hint);
        const type = field(fields, "type", "Thao tác", { options: [["Import", "Nhập thêm"], ["Adjustment", "Kiểm kê"]],
          value: "Import" });
        const quantity = field(fields, "quantity", "Số lượng", { type: "number", required: "", min: 1, max: 2147483647, step: 1 });
        const note = field(fields, "note", "Ghi chú", { multiline: true, wide: true, maxlength: 255, rows: 3 });
        const description = element("p", null, { class: "management-wide" });
        fields.append(description);
        const update = () => {
          const adjustment = type.value === "Adjustment";
          quantity.min = adjustment ? "0" : "1";
          note.required = adjustment;
          description.textContent = adjustment ? "Nhập số lượng tồn sau kiểm kê và ghi lý do." : "Nhập số lượng cần cộng vào tồn kho.";
        };
        type.addEventListener("change", update);
        update();
      },
      payload(form) {
        const controls = form.elements;
        const quantity = controls.quantity.valueAsNumber;
        const type = controls.type.value;
        const note = controls.note.value.trim();
        if (!Number.isInteger(quantity) || quantity < (type === "Import" ? 1 : 0) || quantity > 2147483647) {
          return { error: "Nhập số lượng nguyên hợp lệ.", field: "quantity" };
        }
        if (type === "Adjustment" && !note) return { error: "Nhập lý do kiểm kê.", field: "note" };
        return { body: { type, quantity, note: note || null } };
      }
    });
  }
  form.addEventListener("submit", event => {
    event.preventDefault();
    listQuery = { page: 1, pageSize: 20, keyword: keyword.value.trim() || null, sort: form.elements.sort.value,
      lowStock: form.elements.lowStock.checked || null, outOfStock: form.elements.outOfStock.checked || null };
    void loadStock();
  });
  workspace.append(panel, historyPanel);
  void loadStock();
}
