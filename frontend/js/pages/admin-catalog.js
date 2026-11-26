import { request } from "../api.js";
import { startAdminSession } from "../admin-session.js";
import { authorizedRequest, sessionMessage } from "../auth.js";
import { addVariant, editorFields, editorPayload, element, setOptions, variantSummary } from "../admin-catalog-form.js";
import { renderState } from "../ui.js";

const resources = {
  products: {
    label: "Sản phẩm", id: "productId", name: "productName", columns: ["Danh mục", "Thương hiệu"],
    sorts: [["-createdAt", "Mới nhất"], ["createdAt", "Cũ nhất"], ["productName", "Tên A–Z"],
      ["-productName", "Tên Z–A"], ["-isActive", "Đang hoạt động trước"]]
  },
  categories: { label: "Danh mục", id: "categoryId", name: "categoryName", columns: ["Danh mục cha"] },
  brands: {
    label: "Thương hiệu", id: "brandId", name: "brandName", columns: ["Mô tả"],
    sorts: [["brandName", "Tên A–Z"], ["-brandName", "Tên Z–A"], ["-isActive", "Đang hoạt động trước"]]
  }
};
const selectedView = new URLSearchParams(location.search).get("view");
const view = Object.hasOwn(resources, selectedView) ? selectedView : "products";
const resource = resources[view];
const productView = view === "products";
const categoryView = view === "categories";
const path = "/admin/" + view;
const noun = resource.label.toLocaleLowerCase("vi");
const select = selector => document.querySelector(selector);
const filters = select("[data-filters]");
const state = select("[data-list-state]");
const output = select("[data-list-output]");
const rows = select("[data-rows]");
const notice = select("[data-notice]");
const lookupError = select("[data-lookups-error]");
const reload = select("[data-reload]");
const pagination = select("[data-pagination]");
const create = select("[data-create]");
const previous = select("[data-prev]");
const next = select("[data-next]");
const editor = select("[data-editor]");
const confirm = select("[data-confirm]");
const form = select("[data-editor-form]");
const formFields = select("[data-fields]");
const variantRows = select("[data-variant-rows]");
const existingVariants = select("[data-existing-variants]");
const formError = select("[data-editor-error]");
const confirmError = select("[data-confirm-error]");
const editorState = select("[data-editor-state]");
const dismiss = select("[data-editor-dismiss]");
const save = select("[data-save]");
const hide = select("[data-hide]");
let session;
let listRequest;
let dialogRequest;
let partners;
let optionRequest;
let categories = [];
let editRow;
let hideRow;
let writing = false;
let query = {};

select("[data-title]").textContent = resource.label;
document.title = resource.label + " — Quản trị SportShop";
select(`[data-view="${view}"]`).setAttribute("aria-current", "page");
select("[data-columns]").append(...["Tên", ...resource.columns, "Trạng thái", "Thao tác"].map(label =>
  element("th", label, { scope: "col" })));
filters.hidden = categoryView;
pagination.hidden = categoryView;
for (const filter of filters.querySelectorAll("[data-product-filter]")) filter.hidden = !productView;
if (resource.sorts) setOptions(filters.elements.sort, resource.sorts, null, resource.sorts[0][0]);

function message(target, value = "") {
  target.textContent = value;
  target.hidden = !value;
}

function signal(request = session) {
  const timeout = AbortSignal.timeout(22000);
  return AbortSignal.any(request === session ? [session.signal, timeout] : [session.signal, request.signal, timeout]);
}

function current(owner, request, active) {
  return session === owner && request === active && !request.signal.aborted;
}

function readQuery() {
  if (categoryView) return {};
  const values = filters.elements;
  const base = { page: 1, pageSize: Number(values.pageSize.value), sort: values.sort.value };
  if (!productView) return base;
  return { ...base, keyword: values.keyword.value.trim() || null, categoryId: values.categoryId.value || null,
    brandId: values.brandId.value || null, isActive: values.isActive.value || null };
}

async function readPartners() {
  const operation = signal();
  async function brands() {
    const items = [];
    for (let page = 1; ; page++) {
      const { data } = await authorizedRequest("/admin/brands", { query: { page, pageSize: 50 }, signal: operation });
      items.push(...data.items);
      if (page >= data.pagination.totalPages) return items;
    }
  }
  const [{ data }, brandList] = await Promise.all([authorizedRequest("/admin/categories", { signal: operation }), brands()]);
  return { categories: data, brands: brandList };
}

function loadPartners() {
  if (!productView || partners || optionRequest) return;
  const owner = session;
  message(lookupError);
  optionRequest = readPartners().then(options => {
    if (owner !== session) return;
    partners = options;
    for (const [control, data, id, name] of [
      [filters.elements.categoryId, options.categories, "categoryId", "categoryName"],
      [filters.elements.brandId, options.brands, "brandId", "brandName"]
    ]) {
      setOptions(control, data.map(row => [row[id], row[name] + (row.isActive ? "" : " (đã ẩn)")]), "Tất cả", control.value);
      control.disabled = false;
    }
  }).catch(error => {
    if (owner === session) message(lookupError, "Chưa tải được bộ lọc danh mục và thương hiệu. " + sessionMessage(error));
    return null;
  }).finally(() => { if (owner === session) optionRequest = null; });
}

function showList(items) {
  const parents = new Map(categories.map(row => [row.categoryId, row.categoryName]));
  for (const row of items) {
    const tr = element("tr", null, { "data-id": row[resource.id] });
    const details = productView ? [row.categoryName, row.brandName]
      : [categoryView ? parents.get(row.parentCategoryId) : row.description];
    const status = element("span", row.isActive ? "Đang hoạt động" : "Đã ẩn", { class: "management-status" });
    status.toggleAttribute("data-active", row.isActive);
    const actions = element("div", null, { class: "management-actions" });
    const edit = element("button", "Sửa", { class: "button button-outline", type: "button", "data-edit": "",
      "aria-label": "Sửa " + row[resource.name] });
    edit.addEventListener("click", () => void openEditor(row));
    actions.append(edit);
    if (productView) {
      const detail = new URL("./manage/", location.href);
      detail.searchParams.set("productId", row.productId);
      actions.append(element("a", "Biến thể và ảnh", {
        href: detail.href, class: "button button-outline", "aria-label": "Biến thể và ảnh của " + row.productName
      }));
    }
    if (row.isActive) {
      const disable = element("button", "Ẩn", { class: "button button-outline", type: "button", "data-disable": "",
        "aria-label": "Ẩn " + row[resource.name] });
      disable.addEventListener("click", () => openConfirm(row));
      actions.append(disable);
    }
    const statusCell = element("td"), actionCell = element("td");
    statusCell.append(status);
    actionCell.append(actions);
    tr.append(element("th", row[resource.name], { scope: "row" }), ...details.map(value => element("td", value || "—")),
      statusCell, actionCell);
    rows.append(tr);
  }
}

async function loadList() {
  const owner = session;
  if (!owner) return;
  listRequest?.abort();
  const operation = listRequest = new AbortController();
  const active = () => current(owner, operation, listRequest);
  rows.replaceChildren();
  output.hidden = reload.hidden = true;
  state.hidden = false;
  renderState(state, "loading", "Đang tải danh sách", "");
  try {
    const { data } = await authorizedRequest(path, { query, signal: signal(operation) });
    if (!active()) return;
    const items = categoryView ? data : data.items;
    if (categoryView) categories = items;
    else {
      const { page, totalPages, totalItems } = data.pagination;
      if (page > 1 && page > totalPages) {
        query = { ...query, page: Math.max(1, totalPages) };
        return loadList();
      }
      select("[data-page-info]").textContent = `Trang ${page}/${totalPages} · ${totalItems} kết quả`;
      previous.disabled = page <= 1;
      next.disabled = page >= totalPages;
    }
    if (!items.length) {
      renderState(state, "empty", "Không có " + noun, productView ? "Thử thay đổi bộ lọc." : "");
      reload.hidden = false;
      return;
    }
    showList(items);
    output.hidden = false;
    state.hidden = true;
  } catch (error) {
    if (!active()) return;
    renderState(state, "error", error.status === 403 ? "Không có quyền truy cập" : "Chưa tải được danh sách", sessionMessage(error));
    reload.hidden = false;
  }
}

filters.addEventListener("submit", event => {
  event.preventDefault();
  query = readQuery();
  message(notice);
  loadPartners();
  void loadList();
});
reload.addEventListener("click", () => { loadPartners(); void loadList(); });
for (const [button, change] of [[previous, -1], [next, 1]]) {
  button.addEventListener("click", () => { query = { ...query, page: query.page + change }; void loadList(); });
}

function resetEditor() {
  formFields.replaceChildren();
  variantRows.replaceChildren();
  existingVariants.replaceChildren();
  existingVariants.hidden = true;
  select("[data-add-variant]").onclick = null;
  form.hidden = true;
  message(formError);
  message(editorState);
}

async function openEditor(row) {
  if (!session || writing || editor.open || confirm.open) return;
  editRow = row;
  const owner = session;
  const operation = dialogRequest = new AbortController();
  resetEditor();
  save.disabled = false;
  dismiss.hidden = false;
  select("[data-editor-title]").textContent = (row ? "Sửa " : "Thêm ") + noun;
  message(editorState, "Đang chuẩn bị biểu mẫu…");
  editor.showModal();
  try {
    const options = { categories };
    const opts = { signal: signal(operation) };
    if (productView) {
      const [choices, detail, sizes, colors] = await Promise.all([
        partners || readPartners(),
        row && authorizedRequest(path + "/" + row.productId, opts),
        !row && request("/sizes", { ...opts, auth: false }),
        !row && request("/colors", { ...opts, auth: false })
      ]);
      Object.assign(options, choices, { sizes: sizes?.data, colors: colors?.data });
      if (!current(owner, operation, dialogRequest) || !editor.open) return;
      editRow = detail?.data;
    } else if (categoryView && (output.hidden || listRequest?.signal.aborted)) {
      options.categories = (await authorizedRequest(path, opts)).data;
    }
    if (!current(owner, operation, dialogRequest) || !editor.open) return;
    if (productView && !row && (!options.sizes.length || !options.colors.length)) {
      message(editorState, "Cần có ít nhất một kích thước và một màu đang hoạt động để thêm sản phẩm.");
      return;
    }
    editorFields(formFields, view, editRow, options);
    const variants = select("[data-variants]");
    variants.hidden = !productView || Boolean(row);
    if (!variants.hidden) {
      addVariant(variantRows, options);
      select("[data-add-variant]").onclick = () => addVariant(variantRows, options).querySelector("select").focus();
    }
    if (productView && row) variantSummary(existingVariants, editRow.variants);
    dismiss.hidden = true;
    message(editorState);
    form.hidden = false;
    formFields.querySelector("input")?.focus();
  } catch (error) {
    if (current(owner, operation, dialogRequest) && editor.open) message(editorState, sessionMessage(error));
  }
}

function openConfirm(row) {
  if (!session || writing || editor.open || confirm.open) return;
  hideRow = row;
  dialogRequest = new AbortController();
  hide.disabled = false;
  select("[data-confirm-title]").textContent = "Ẩn " + noun + "?";
  select("[data-confirm-text]").textContent = productView
    ? `“${row[resource.name]}” sẽ ngừng bán. Đơn cũ và tồn kho được giữ lại.`
    : `“${row[resource.name]}” sẽ ngừng hoạt động. Hệ thống sẽ kiểm tra các dữ liệu đang tham chiếu.`;
  message(confirmError);
  confirm.showModal();
}

function fieldErrors(summary, fields, intro) {
  message(summary, [intro, ...fields.map(field => field.message)].filter(Boolean).join(" "));
  for (const field of fields) field.control?.setAttribute("aria-invalid", "true");
  fields.find(field => field.control)?.control.focus();
}

function serverFields(fields) {
  const controls = Array.from(formFields.querySelectorAll("input, select, textarea"));
  return fields.map(({ field, message }) => {
    const match = /^(?:variants\[(\d+)\]\.)?([a-z]+)$/i.exec(field);
    const options = match?.[1] == null ? controls
      : Array.from(variantRows.children[Number(match[1])]?.querySelectorAll("input, select") ?? []);
    return { message, control: options.find(input => input.name.toLowerCase() === match?.[2].toLowerCase()) };
  });
}

async function write({ row, method, body, dialog, button, errorRegion }) {
  if (!session || writing || button.disabled) return;
  const owner = session;
  const operation = dialogRequest;
  const active = () => current(owner, operation, dialogRequest);
  writing = button.disabled = true;
  const editorControls = dialog === editor ? [...form.elements].filter(control => !control.disabled) : [];
  for (const control of editorControls) control.disabled = true;
  let uncertain = false;
  message(notice);
  message(errorRegion);
  try {
    const expectedStatus = method === "POST" ? 201 : 200;
    await authorizedRequest(path + (row ? "/" + row[resource.id] : ""), { method, body, expectedStatus, signal: signal(operation) });
    if (!active()) return;
    dialog.close();
    message(notice, (method === "POST" ? "Đã thêm " : method === "PUT" ? "Đã lưu " : "Đã ẩn ") + noun + ".");
    void loadList();
  } catch (error) {
    if (!active()) return;
    uncertain = !error.status || error.status >= 500 || error.code === "INVALID_RESPONSE";
    if (uncertain) {
      message(errorRegion, "Chưa thể xác nhận thay đổi đã được lưu. Danh sách đang được tải lại để đối chiếu trước khi thao tác tiếp.");
      void loadList();
    } else if (error.status === 400 && dialog === editor && error.fields?.length) {
      for (const control of editorControls) control.disabled = false;
      fieldErrors(errorRegion, serverFields(error.fields), sessionMessage(error));
    } else {
      message(errorRegion, sessionMessage(error));
      if (error.status === 404) void loadList();
    }
  } finally {
    if (session === owner) writing = false;
    if (active()) {
      for (const control of editorControls) control.disabled = false;
      button.disabled = uncertain;
    }
  }
}

create.addEventListener("click", () => void openEditor());
form.addEventListener("submit", event => {
  event.preventDefault();
  if (writing || save.disabled || !editor.open) return;
  for (const field of form.querySelectorAll("[aria-invalid]")) field.removeAttribute("aria-invalid");
  const { body, fields } = editorPayload(formFields, variantRows, view);
  if (fields.length) { fieldErrors(formError, fields); return; }
  void write({ row: editRow, method: editRow ? "PUT" : "POST", body, dialog: editor, button: save, errorRegion: formError });
});
hide.addEventListener("click", () => {
  if (!confirm.open || !hideRow) return;
  void write({ row: hideRow, method: "DELETE", dialog: confirm, button: hide, errorRegion: confirmError });
});
for (const dialog of [editor, confirm]) {
  dialog.addEventListener("cancel", event => { if (writing) event.preventDefault(); });
  dialog.addEventListener("close", () => {
    if (editor.open || confirm.open) return;
    dialogRequest?.abort();
    dialogRequest = editRow = hideRow = null;
    if (dialog === editor) resetEditor();
    else {
      select("[data-confirm-text]").textContent = "";
      message(confirmError);
    }
  });
}
for (const [button, dialog] of [
  [select("[data-editor-close]"), editor], [dismiss, editor], [select("[data-confirm-close]"), confirm]
]) button.addEventListener("click", () => { if (!writing) dialog.close(); });

startAdminSession({
  account: new URL("../../account/", location.href),
  view,
  clear() {
    session?.abort();
    session = null;
    listRequest = dialogRequest = optionRequest = partners = editRow = hideRow = null;
    categories = [];
    writing = false;
    if (editor.open) editor.close();
    if (confirm.open) confirm.close();
    resetEditor();
    select("[data-confirm-text]").textContent = "";
    message(confirmError);
    rows.replaceChildren();
    select("[data-page-info]").textContent = "";
    for (const control of [filters.elements.categoryId, filters.elements.brandId]) {
      setOptions(control, [], "Tất cả");
      control.disabled = true;
    }
    filters.reset();
    if (resource.sorts) filters.elements.sort.value = resource.sorts[0][0];
    for (const node of [output, state]) node.hidden = true;
    message(notice);
    message(lookupError);
  },
  load() {
    session = new AbortController();
    query = readQuery();
    loadPartners();
    void loadList();
  }
});
