import { authorizedRequest, sessionMessage } from "./auth.js";
import { element } from "./admin-catalog-form.js";
import { renderState } from "./ui.js";

export function button(label, attributes = {}) {
  return element("button", label, { type: "button", class: "button button-outline", ...attributes });
}

export function table(title, columns) {
  const region = element("div", null, { class: "admin-table-scroll", tabindex: "0", role: "region", "aria-label": title });
  const grid = element("table", null, { class: "admin-table management-table" });
  const header = grid.createTHead().insertRow();
  header.append(...columns.map(column => element("th", column, { scope: "col" })));
  const body = grid.createTBody();
  region.append(grid);
  return { region, body };
}

export function tableRow(body, ...cells) {
  const row = body.insertRow();
  for (const value of cells) {
    const cell = row.insertCell();
    if (value instanceof Node) cell.append(value);
    else cell.textContent = value ?? "—";
  }
  return row;
}

export function pagination(change) {
  const root = element("div", null, { class: "management-pagination", "aria-label": "Phân trang" });
  const label = element("p", null, { role: "status" });
  const prev = button("Trang trước", { "data-prev": "" });
  const next = button("Trang sau", { "data-next": "" });
  const actions = element("div");
  actions.append(prev, next);
  root.append(label, actions);
  let page;
  prev.addEventListener("click", () => change(page - 1));
  next.addEventListener("click", () => change(page + 1));
  return {
    root,
    update(meta) {
      page = meta.page;
      label.textContent = `Trang ${page}/${meta.totalPages} · ${meta.totalItems} kết quả`;
      prev.disabled = page <= 1;
      next.disabled = page >= meta.totalPages;
    }
  };
}

export function readPanel(panel, output, session, read, render, empty) {
  const state = element("div", null, { class: "state-panel admin-state" });
  const status = element("div", null, { role: "status" });
  status.append(element("strong", null, { "data-state-title": "" }), element("p", null, { "data-state-message": "" }));
  const retry = button("Tải lại");
  state.append(element("span", null, { class: "state-icon", "aria-hidden": "true" }), status, retry);
  panel.append(state, output);
  let active;
  async function load() {
    if (session.aborted) return;
    active?.abort();
    const operation = active = new AbortController();
    const cancelled = () => session.aborted || operation.signal.aborted;
    retry.hidden = output.hidden = true;
    state.hidden = false;
    renderState(state, "loading", "Đang tải dữ liệu…", "");
    try {
      const data = await read(AbortSignal.any([session, operation.signal, AbortSignal.timeout(15000)]));
      if (cancelled()) return;
      const hasData = render(data);
      if (cancelled()) return;
      if (hasData === false) {
        renderState(state, "empty", empty, "");
        retry.hidden = false;
      } else {
        output.hidden = false;
        state.hidden = true;
      }
    } catch (error) {
      if (cancelled()) return;
      const title = error.status === 403 ? "Không có quyền truy cập"
        : error.status === 404 ? "Không tìm thấy dữ liệu" : "Chưa tải được dữ liệu";
      renderState(state, "error", title, sessionMessage(error));
      retry.hidden = false;
    }
  }
  retry.addEventListener("click", () => void load());
  return load;
}

export function operationDialog(feedback) {
  const dialog = document.querySelector("[data-operation]");
  const title = dialog.querySelector("[data-operation-title]");
  const loading = dialog.querySelector("[data-operation-loading]");
  const form = dialog.querySelector("form");
  const fields = dialog.querySelector("fieldset");
  const error = dialog.querySelector("[data-operation-error]");
  const save = dialog.querySelector("[data-operation-save]");
  const close = dialog.querySelector("[data-operation-close]");
  let active;
  let writing = false;

  function clear() {
    active?.controller.abort();
    active?.dispose?.();
    active = null;
    writing = false;
    fields.replaceChildren();
    error.textContent = title.textContent = "";
    error.hidden = loading.hidden = true;
    close.disabled = fields.disabled = false;
    save.hidden = save.disabled = false;
  }
  function dismiss() {
    if (dialog.open) dialog.close();
    clear();
  }
  function showError(message) {
    error.textContent = message;
    error.hidden = !message;
  }
  close.addEventListener("click", () => { if (!writing) dismiss(); });
  dialog.addEventListener("cancel", event => {
    event.preventDefault();
    if (!writing) dismiss();
  });
  form.addEventListener("submit", async event => {
    event.preventDefault();
    if (!active || writing || save.disabled) return;
    const command = active;
    showError("");
    for (const invalid of fields.querySelectorAll("[aria-invalid]")) invalid.removeAttribute("aria-invalid");
    const result = command.payload?.(form) ?? {};
    if (result.error) {
      showError(result.error);
      const control = form.elements.namedItem(result.field);
      control?.setAttribute("aria-invalid", "true");
      control?.focus();
      return;
    }
    writing = save.disabled = fields.disabled = close.disabled = true;
    feedback("");
    let requireNewConfirmation = false;
    let invalidControl;
    try {
      await authorizedRequest(command.path, {
        method: command.method, body: result.body, expectedStatus: command.status ?? 200,
        signal: AbortSignal.any([command.signal, AbortSignal.timeout(25000)])
      });
      if (active !== command || command.signal.aborted) return;
      dismiss();
      feedback(command.success);
      command.reload();
    } catch (failure) {
      if (active !== command || command.signal.aborted) return;
      if (!failure.status || failure.status >= 500 || failure.code === "INVALID_RESPONSE") {
        requireNewConfirmation = true;
        const warning = command.unknownMessage ??
          "Chưa xác định được kết quả. Dữ liệu đang được tải lại để bạn đối chiếu trước khi thao tác tiếp.";
        showError(warning);
        feedback(warning, true);
        command.reload(true);
      } else {
        const messages = failure.fields?.map(field => field.message) ?? [];
        showError([...new Set([sessionMessage(failure), ...messages])].join(" "));
        const named = failure.fields?.[0]?.field.toLowerCase();
        invalidControl = [...fields.elements].find(input => input.name.toLowerCase() === named);
        if (command.reconcileRejection && [400, 404, 409].includes(failure.status)) {
          requireNewConfirmation = true;
          command.reload();
        } else if (failure.status === 404 || failure.code === "CONCURRENCY_CONFLICT") command.reload();
      }
    } finally {
      if (active === command) {
        writing = close.disabled = false;
        fields.disabled = save.disabled = requireNewConfirmation;
        invalidControl?.setAttribute("aria-invalid", "true");
        invalidControl?.focus();
      }
    }
  });

  return {
    dismiss,
    async open(session, config) {
      if (session.aborted || dialog.open) return;
      clear();
      const controller = new AbortController();
      const signal = AbortSignal.any([session, controller.signal]);
      const command = active = { ...config, controller, signal };
      title.textContent = config.title;
      save.textContent = config.label ?? "Lưu";
      save.hidden = save.disabled = Boolean(config.prepare);
      loading.hidden = !config.prepare;
      dialog.showModal();
      try {
        const data = config.prepare ? await config.prepare(signal) : undefined;
        if (active !== command || signal.aborted) return;
        command.dispose = config.render(fields, data, signal, showError);
        save.hidden = save.disabled = false;
        const initial = fields.querySelector("input:not([type=checkbox]), select, textarea");
        (initial || close).focus();
      } catch (failure) {
        if (active === command && !signal.aborted) {
          save.hidden = true;
          showError(sessionMessage(failure));
        }
      } finally {
        if (active === command) loading.hidden = true;
      }
    }
  };
}
