import { money } from "./catalog.js";

export function element(tag, text, attributes = {}) {
  const node = document.createElement(tag);
  if (text != null) node.textContent = text;
  for (const [name, value] of Object.entries(attributes)) node.setAttribute(name, value);
  return node;
}

export function setOptions(select, options, placeholder, selected = "") {
  if (placeholder != null) select.replaceChildren(new Option(placeholder, ""));
  else select.replaceChildren();
  for (const [value, label, disabled] of options) {
    const option = new Option(label, String(value));
    option.disabled = Boolean(disabled);
    select.append(option);
  }
  select.value = String(selected);
}

export function field(container, name, label, { value, options, placeholder, multiline, wide, ...attributes } = {}) {
  const input = element(options ? "select" : multiline ? "textarea" : "input", null, { name, ...attributes });
  if (options) setOptions(input, options, placeholder, value);
  else if (attributes.type === "checkbox") input.checked = value !== false;
  else input.value = value ?? "";
  const wrapper = element("label", label);
  if (wide) wrapper.classList.add("management-wide");
  if (attributes.type === "checkbox") wrapper.classList.add("management-checkbox");
  wrapper.append(input);
  container.append(wrapper);
  return input;
}

function productOptions(rows, idKey, nameKey, current) {
  return rows.filter(row => row.isActive || row[idKey] === current).map(row =>
    [row[idKey], row[nameKey] + (row.isActive ? "" : " (đã ẩn)"), !row.isActive]);
}

function canParent(row, rowId, indexed) {
  const seen = new Set();
  for (let parent = row; parent; parent = indexed.get(parent.parentCategoryId)) {
    if (parent.categoryId === rowId || seen.has(parent.categoryId)) return false;
    seen.add(parent.categoryId);
  }
  return true;
}

export function editorFields(container, view, row, lookups) {
  const key = { products: "productName", categories: "categoryName", brands: "brandName" }[view];
  field(container, key, "Tên", { required: "", maxlength: view === "products" ? 200 : 100, value: row?.[key], wide: true });
  if (view === "products") {
    field(container, "categoryId", "Danh mục", { required: "", placeholder: "Chọn danh mục",
      options: productOptions(lookups.categories, "categoryId", "categoryName", row?.categoryId), value: row?.categoryId });
    field(container, "brandId", "Thương hiệu", { required: "", placeholder: "Chọn thương hiệu",
      options: productOptions(lookups.brands, "brandId", "brandName", row?.brandId), value: row?.brandId });
  }
  if (view === "categories") {
    const indexed = new Map(lookups.categories.map(category => [category.categoryId, category]));
    field(container, "parentCategoryId", "Danh mục cha", { placeholder: "Không có",
      options: lookups.categories.filter(category => canParent(category, row?.categoryId, indexed))
        .map(category => [category.categoryId, category.categoryName]), value: row?.parentCategoryId ?? "" });
  }
  if (view === "brands") field(container, "logoUrl", "URL logo", { maxlength: 500, value: row?.logoUrl, wide: true });
  field(container, "description", "Mô tả", { multiline: true, wide: true, rows: 3, value: row?.description,
    ...(view === "products" ? {} : { maxlength: 500 }) });
  const status = field(container, "isActive", "Đang hoạt động", { type: "checkbox", value: row?.isActive });
  if (row?.isActive) {
    status.disabled = true;
    container.append(element("p", "Để ngừng hoạt động, dùng thao tác Ẩn trong danh sách."));
  }
}

export function variantSummary(container, variants) {
  if (!variants?.length) return;
  const list = element("ul", null, { class: "management-variant-summary" });
  for (const variant of variants) {
    const info = [variant.sku, variant.colorName, variant.sizeName, money(variant.price),
      variant.isActive ? "Đang hoạt động" : "Đã ẩn"];
    list.append(element("li", info.join(" · ")));
  }
  container.append(element("h3", "Biến thể hiện có"), list);
  container.hidden = false;
}

export function variantFields(fields, lookups, variant) {
  if (!variant) {
    field(fields, "sizeId", "Kích thước", { required: "", placeholder: "Chọn kích thước", options: lookups.sizes.map(row => [row.sizeId, row.sizeName]) });
    field(fields, "colorId", "Màu sắc", { required: "", placeholder: "Chọn màu", options: lookups.colors.map(row => [row.colorId, row.colorName]) });
  }
  field(fields, "sku", "SKU", { required: "", maxlength: 50, pattern: "[A-Za-z0-9\\-]+", value: variant?.sku });
  field(fields, "price", "Giá (₫)", { type: "number", required: "", min: 0, max: 1000000000, step: "0.01", value: variant?.price });
  if (!variant) field(fields, "initialStock", "Tồn kho ban đầu", { type: "number", required: "", min: 0, max: 2147483647, step: 1, value: 0 });
}

export function addVariant(container, lookups) {
  const variant = element("fieldset", null, { class: "management-variant", "data-variant": "" });
  const fields = element("div", null, { class: "management-fields" });
  variantFields(fields, lookups);
  const remove = element("button", "Bỏ biến thể", { type: "button", class: "button button-outline" });
  remove.addEventListener("click", () => {
    const neighbor = variant.previousElementSibling || variant.nextElementSibling;
    variant.remove();
    labelVariants(container);
    neighbor?.querySelector("select").focus();
  });
  variant.append(element("legend"), fields, remove);
  container.append(variant);
  labelVariants(container);
  return variant;
}

function labelVariants(container) {
  Array.from(container.children).forEach((variant, index) => {
    variant.querySelector("legend").textContent = "Biến thể " + (index + 1);
    variant.querySelector("button").disabled = container.children.length === 1;
  });
}

export function editorPayload(container, variantRows, view) {
  const inputs = new Map(Array.from(container.querySelectorAll("input, select, textarea"), input => [input.name, input]));
  const fields = [];
  const key = { products: "productName", categories: "categoryName", brands: "brandName" }[view];
  const name = inputs.get(key).value.trim();
  const required = (value, control, message) => { if (!value) fields.push({ control, message }); };
  required(name, inputs.get(key), "Vui lòng nhập tên.");
  const body = { [key]: name, description: inputs.get("description").value || null, isActive: inputs.get("isActive").checked };
  if (view === "categories") body.parentCategoryId = Number(inputs.get("parentCategoryId").value) || null;
  if (view === "brands") body.logoUrl = inputs.get("logoUrl").value.trim() || null;
  if (view !== "products") return { body, fields };
  for (const id of ["categoryId", "brandId"]) {
    const select = inputs.get(id);
    body[id] = Number(select.value);
    if (!body[id] || select.selectedOptions[0]?.disabled) {
      fields.push({ control: select, message: id === "categoryId"
        ? "Vui lòng chọn danh mục đang hoạt động." : "Vui lòng chọn thương hiệu đang hoạt động." });
    }
  }
  if (variantRows.children.length) {
    const combinations = new Set();
    body.variants = Array.from(variantRows.children, row => {
      const value = name => row.querySelector(`[name="${name}"]`).value;
      const sizeId = Number(value("sizeId")), colorId = Number(value("colorId"));
      const key = sizeId + ":" + colorId;
      if (combinations.has(key)) fields.push({ control: row.querySelector("[name=colorId]"), message: "Tổ hợp kích thước và màu đã được chọn." });
      combinations.add(key);
      return { sizeId, colorId, sku: value("sku"), price: Number(value("price")), initialStock: Number(value("initialStock")) };
    });
  }
  return { body, fields };
}
