import { element, field, variantFields } from "../admin-catalog-form.js";
import { button, readPanel, table, tableRow } from "../admin-stock-ui.js";
import { ApiError } from "../api.js";
import { authorizedRequest } from "../auth.js";
import { getCatalog, money, productImage } from "../catalog.js";

const maxImages = 10;
const maxBytes = 2 * 1024 * 1024;
const maxSide = 4000;
const number = new Intl.NumberFormat("vi-VN");

export function showProductAssets({ workspace, dialog, session }, productId) {
  const path = `/admin/products/${productId}`;
  const panel = element("section", null, { class: "admin-panel catalog-management", "aria-label": "Biến thể và ảnh sản phẩm" });
  const header = element("div", null, { class: "admin-panel-heading" });
  const name = element("h2", "Thông tin sản phẩm", { "data-product-name": "" });
  header.append(name, element("a", "← Danh sách sản phẩm", { href: "../", class: "button button-outline" }));
  panel.append(header);
  const output = element("div", null, { class: "stock-workspace", "data-assets-output": "" });
  const load = readPanel(panel, output, session,
    async signal => (await authorizedRequest(path, { signal })).data,
    product => {
      name.textContent = product.productName;
      output.replaceChildren();
      const variantSection = element("section", null, { class: "catalog-management", "aria-label": "Biến thể" });
      const variantHead = element("div", null, { class: "admin-panel-heading" });
      const add = button("Thêm biến thể", { "data-add-product-variant": "", class: "button button-primary" });
      add.addEventListener("click", () => editVariant(product));
      variantHead.append(element("h3", "Biến thể"), add);
      variantSection.append(variantHead);
      const variants = table("Danh sách biến thể", ["SKU", "Size", "Màu", "Giá", "Tồn thực tế", "Trạng thái", "Thao tác"]);
      for (const variant of product.variants) {
        const status = element("span", variant.isActive ? "Đang hoạt động" : "Đã ngừng bán", { class: "management-status" });
        status.toggleAttribute("data-active", variant.isActive);
        const actions = element("div", null, { class: "management-actions" });
        const edit = button("Sửa", { "data-edit-variant": "", "aria-label": "Sửa biến thể " + variant.sku });
        edit.addEventListener("click", () => editVariant(product, variant));
        const inventory = new URL(location.pathname, location.href);
        inventory.searchParams.set("view", "inventory");
        inventory.searchParams.set("keyword", variant.sku);
        actions.append(edit, element("a", "Tồn kho", { href: inventory.href, class: "button button-outline",
          "aria-label": "Mở tồn kho " + variant.sku }));
        if (variant.isActive) {
          const hide = button("Ngừng bán", { "data-hide-variant": "", "aria-label": "Ngừng bán " + variant.sku });
          hide.addEventListener("click", () => confirmDelete("Ngừng bán biến thể",
            `Ngừng bán “${variant.sku}”? Tồn kho và lịch sử giao dịch vẫn được giữ.`,
            `${path}/variants/${variant.variantId}`, "Ngừng bán", "Đã ngừng bán biến thể."));
          actions.append(hide);
        }
        const row = tableRow(variants.body, variant.sku, variant.sizeName, variant.colorName, money(variant.price),
          number.format(variant.quantityOnHand), status, actions);
        row.dataset.variantId = variant.variantId;
      }
      variantSection.append(product.variants.length ? variants.region : element("p", "Chưa có biến thể"));
      output.append(variantSection, photos(product));
    }, "Không tìm thấy sản phẩm");

  function editVariant(product, variant) {
    const method = variant ? "PUT" : "POST";
    void dialog.open(session, {
      title: variant ? "Sửa biến thể · " + variant.sku : "Thêm biến thể",
      path: `${path}/variants${variant ? "/" + variant.variantId : ""}`, method,
      status: variant ? 200 : 201, success: variant ? "Đã lưu biến thể." : "Đã thêm biến thể.", reload: load,
      prepare: variant ? null : async signal => {
        const [sizes, colors] = await Promise.all([getCatalog("/sizes", { signal }), getCatalog("/colors", { signal })]);
        if (!sizes.length || !colors.length) throw new ApiError("Cần có ít nhất một kích thước và một màu đang hoạt động.", {});
        return { sizes, colors };
      },
      render(fields, lookups) {
        if (variant) fields.append(element("p", variant.sizeName + " / " + variant.colorName, { class: "management-wide" }));
        variantFields(fields, lookups, variant);
        field(fields, "isActive", "Đang hoạt động", { type: "checkbox", value: variant?.isActive });
      },
      payload(form) {
        const values = form.elements;
        const body = { sku: values.sku.value, price: values.price.valueAsNumber, isActive: values.isActive.checked };
        if (!variant) {
          body.sizeId = Number(values.sizeId.value);
          body.colorId = Number(values.colorId.value);
          body.initialStock = values.initialStock.valueAsNumber;
          if (product.variants.some(row => row.sizeId === body.sizeId && row.colorId === body.colorId)) {
            return { error: "Tổ hợp kích thước và màu đã tồn tại, kể cả biến thể đã ngừng bán.", field: "colorId" };
          }
        }
        return { body };
      }
    });
  }

  function confirmDelete(title, description, url, label, success) {
    void dialog.open(session, {
      title, path: url, method: "DELETE", label, success, reload: load,
      render(fields) { fields.append(element("p", description, { class: "management-wide" })); }
    });
  }

  function colorsFor(product) {
    return new Map(product.variants.map(variant => [variant.colorId, variant.colorName]));
  }

  function photos(product) {
    const section = element("section", null, { class: "catalog-management", "aria-label": "Ảnh sản phẩm" });
    const heading = element("div", null, { class: "admin-panel-heading" });
    const add = button("Tải ảnh lên", { class: "button button-primary", "data-upload-image": "" });
    add.disabled = product.images.length >= maxImages;
    add.addEventListener("click", () => upload(product));
    heading.append(element("h3", "Ảnh sản phẩm"), add);
    section.append(heading, element("p", `${product.images.length}/${maxImages} ảnh · JPG, PNG hoặc WEBP · tối đa 2 MB và 4000 × 4000 px`));
    if (!product.images.length) {
      section.append(element("p", "Chưa có ảnh"));
      return section;
    }
    const gallery = element("div", null, { class: "stock-photos" });
    const colors = colorsFor(product);
    for (const image of product.images) {
      const tile = element("article", null, { class: "stock-photo", "data-image-id": image.imageId });
      const caption = image.colorId == null ? "Ảnh chung" : colors.get(image.colorId) ?? "Ảnh theo màu";
      const header = element("div", null, { class: "admin-panel-heading" });
      header.append(element("strong", caption));
      if (image.isPrimary) header.append(element("span", "Ảnh đại diện", { class: "management-status", "data-active": "" }));
      const remove = button("Xóa ảnh", { "data-delete-image": "", "aria-label": "Xóa ảnh " + image.imageId });
      remove.addEventListener("click", () => confirmDelete("Xóa ảnh sản phẩm",
        image.isPrimary ? "Xóa ảnh đại diện này? Bạn có thể tải ảnh đại diện mới sau đó." : "Xóa liên kết của ảnh này với sản phẩm?",
        `${path}/images/${image.imageId}`, "Xóa ảnh", "Đã xóa ảnh khỏi sản phẩm."));
      tile.append(productImage(image.imageUrl, product.productName + " · " + caption), header, remove);
      gallery.append(tile);
    }
    section.append(gallery);
    return section;
  }

  function upload(product) {
    let checkedFile;
    void dialog.open(session, {
      title: "Tải ảnh sản phẩm", path: path + "/images", method: "POST", status: 201,
      label: "Tải lên", success: "Đã tải ảnh lên.", reload: load,
      render(fields, _, signal, showError) {
        const input = field(fields, "ImageFile", "Tệp ảnh", {
          type: "file", required: "", accept: ".jpg,.jpeg,.png,.webp", wide: true
        });
        field(fields, "colorId", "Màu của ảnh", { options: [...colorsFor(product)], placeholder: "Ảnh chung" });
        field(fields, "isPrimary", "Dùng làm ảnh đại diện", { type: "checkbox", value: false });
        fields.append(element("p", "Mỗi lần tải một ảnh. Nếu chọn làm đại diện, ảnh mới sẽ thay ảnh đại diện hiện tại.",
          { class: "management-wide" }));
        const preview = element("figure", null, { class: "stock-preview management-wide", hidden: "" });
        const previewImage = element("img", null, { alt: "Xem trước ảnh đã chọn", "data-image-preview": "" });
        const caption = element("figcaption");
        preview.append(previewImage, caption);
        fields.append(preview);
        let version = 0;
        let previewUrl;
        function clearPreview() {
          if (previewUrl) URL.revokeObjectURL(previewUrl);
          previewUrl = null;
          checkedFile = null;
          preview.hidden = true;
          previewImage.removeAttribute("src");
          caption.textContent = "";
        }
        input.addEventListener("change", async () => {
          const revision = ++version;
          clearPreview();
          showError("");
          if (!input.files.length) return;
          const file = input.files[0];
          try {
            if (input.files.length !== 1) throw new Error("Chọn đúng một tệp ảnh.");
            const { width, height } = await inspectImage(file);
            if (revision !== version || signal.aborted) return;
            checkedFile = file;
            previewUrl = URL.createObjectURL(file);
            previewImage.src = previewUrl;
            caption.textContent = `${file.name} · ${width} × ${height} px`;
            preview.hidden = false;
          } catch (error) {
            if (revision !== version || signal.aborted) return;
            input.value = "";
            showError(error.message);
          }
        });
        return clearPreview;
      },
      payload(form) {
        if (!checkedFile || checkedFile !== form.elements.ImageFile.files[0]) {
          return { error: "Chờ kiểm tra xong hoặc chọn một ảnh hợp lệ.", field: "ImageFile" };
        }
        const body = new FormData();
        body.append("ImageFile", checkedFile);
        if (form.elements.colorId.value) body.append("colorId", form.elements.colorId.value);
        body.append("isPrimary", String(form.elements.isPrimary.checked));
        return { body };
      }
    });
  }
  workspace.append(panel);
  void load();
}

async function inspectImage(file) {
  if (file.size > maxBytes) throw new Error("Ảnh vượt quá 2 MB.");
  const header = new Uint8Array(await file.slice(0, 12).arrayBuffer());
  const starts = (bytes, offset = 0) => bytes.every((value, index) => header[offset + index] === value);
  const ext = file.name.split(".").at(-1).toLowerCase();
  const jpg = (ext === "jpg" || ext === "jpeg") && starts([255, 216, 255]);
  const png = ext === "png" && starts([137, 80, 78, 71, 13, 10, 26, 10]);
  const webp = ext === "webp" && starts([82, 73, 70, 70]) && starts([87, 69, 66, 80], 8);
  if (!jpg && !png && !webp) throw new Error("Chỉ chấp nhận ảnh JPG, PNG hoặc WEBP có phần mở rộng đúng định dạng.");
  let bitmap;
  try {
    bitmap = await createImageBitmap(file);
    const { width, height } = bitmap;
    if (width > maxSide || height > maxSide) throw new Error("Ảnh vượt kích thước tối đa 4000 × 4000 px.");
    return { width, height };
  } catch (error) {
    if (!bitmap) throw new Error("Không đọc được tệp ảnh.");
    throw error;
  } finally {
    bitmap?.close();
  }
}
