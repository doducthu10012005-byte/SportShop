import { changePassword, login, logout, register, restoreSession, sessionMessage, watchSession } from "../auth.js";

const views = new Map([...document.querySelectorAll("[data-account-view]")].map(view => [view.dataset.accountView, view]));
const notice = document.querySelector("[data-session-notice]");
const progress = document.querySelector("[data-account-progress]");
let currentSession;
let shownView;
let registrationMessage = "";

function render() {
  const { phase, user, message } = currentSession;
  const busy = ["idle", "checking", "signing-out"].includes(phase);
  const forced = user?.mustChangePassword === true;
  let page = location.hash.slice(1);
  if (forced) page = "password";
  else if (user) page = page === "password" ? page : "profile";
  else if (page !== "register") page = "login";

  if (!busy && location.hash !== "#" + page) history.replaceState(null, "", "#" + page);
  progress.hidden = !busy;
  progress.querySelector("p").textContent = phase === "signing-out" ? "Đang đăng xuất…" : "Đang kiểm tra phiên đăng nhập…";
  notice.textContent = busy ? "" : registrationMessage || message;
  notice.hidden = !notice.textContent;
  document.querySelector("[data-store-link]").hidden = busy || forced;
  document.querySelector("[data-profile-back]").hidden = forced;
  document.querySelector("[data-forced-note]").hidden = !forced;
  document.querySelector("[data-password-title]").textContent = forced ? "Đổi mật khẩu bắt buộc" : "Đổi mật khẩu";
  document.querySelector("[data-admin-link]").hidden = busy || forced || user?.role !== "Admin";

  for (const [name, section] of views) section.hidden = busy || name !== page;
  if (user) {
    document.querySelector("[data-profile-name]").textContent = user.fullName || "—";
    document.querySelector("[data-profile-email]").textContent = user.email || "—";
    document.querySelector("[data-profile-phone]").textContent = user.phoneNumber || "—";
    document.querySelector("[data-profile-role]").textContent = user.role === "Admin" ? "Quản trị viên" : "Khách hàng";
  } else {
    for (const field of ["name", "email", "phone", "role"]) document.querySelector("[data-profile-" + field + "]").textContent = "";
  }
  if (!busy && shownView !== page) {
    shownView = page;
    views.get(page).querySelector("h1").focus();
  }
}

watchSession(session => {
  currentSession = session;
  if (session.user) registrationMessage = "";
  render();
});
window.addEventListener("hashchange", render);
document.querySelector(".skip-link").addEventListener("click", event => {
  event.preventDefault();
  document.querySelector("#main").focus();
});

for (const button of document.querySelectorAll("[data-logout]")) {
  button.addEventListener("click", () => { void logout(); });
}

function showError(form, text, fields = []) {
  const alert = form.querySelector("[data-form-error]");
  alert.textContent = text;
  alert.hidden = !text;
  for (const hint of form.querySelectorAll("[data-field-error]")) {
    const name = hint.dataset.fieldError;
    const input = form.elements.namedItem(name);
    const field = fields.find(item => item.field === name);
    hint.id = form.dataset.accountForm + "-" + name + "-error";
    hint.textContent = field?.message ?? "";
    const existingHints = (input.getAttribute("aria-describedby") ?? "").split(" ").filter(id => id && id !== hint.id);
    if (field) {
      input.setAttribute("aria-invalid", "true");
      existingHints.push(hint.id);
    } else input.removeAttribute("aria-invalid");
    if (existingHints.length) input.setAttribute("aria-describedby", existingHints.join(" "));
    else input.removeAttribute("aria-describedby");
  }
  form.querySelector('[aria-invalid="true"]')?.focus();
}

function validate(form, values) {
  const errors = [];
  function add(field, message) { errors.push({ field, message }); }
  for (const input of form.querySelectorAll("input[required]")) {
    if (!values[input.name]) add(input.name, "Vui lòng nhập trường này.");
  }
  if (values.email && form.elements.namedItem("email").validity.typeMismatch)
    add("email", "Email không đúng định dạng.");
  if (values.fullName && values.fullName.length < 2) add("fullName", "Họ tên cần ít nhất 2 ký tự.");
  if (values.phoneNumber && !/^(?:0|\+84)[0-9]{9}$/.test(values.phoneNumber))
    add("phoneNumber", "Số điện thoại phải bắt đầu bằng 0 hoặc +84, theo sau là 9 chữ số.");
  const kind = form.dataset.accountForm;
  if (kind === "login") return errors;
  const passwordField = kind === "register" ? "password" : "newPassword";
  const confirmField = kind === "register" ? "confirmPassword" : "confirmNewPassword";
  const password = values[passwordField];
  if (password && (password.length < 8 || password.length > 64 || !/\p{Lu}/u.test(password) ||
    !/\p{Ll}/u.test(password) || !/\p{Nd}/u.test(password))) {
    add(passwordField, "Dùng 8–64 ký tự, có chữ hoa, chữ thường và chữ số.");
  }
  if (values[confirmField] && values[confirmField] !== password) add(confirmField, "Mật khẩu nhập lại không khớp.");
  return errors;
}

for (const form of document.querySelectorAll("[data-account-form]")) {
  form.addEventListener("submit", async event => {
    event.preventDefault();
    const submit = form.querySelector('button[type="submit"]');
    if (submit.disabled) return;
    const values = Object.fromEntries(new FormData(form));
    for (const name of ["email", "fullName", "phoneNumber"]) {
      if (typeof values[name] === "string") values[name] = values[name].trim();
    }
    registrationMessage = "";
    notice.hidden = true;
    const fields = validate(form, values);
    showError(form, fields.length ? "Vui lòng kiểm tra thông tin đã nhập." : "", fields);
    if (fields.length) return;

    if (values.phoneNumber === "") delete values.phoneNumber;
    const label = submit.textContent;
    submit.disabled = true;
    submit.textContent = "Đang xử lý…";
    form.setAttribute("aria-busy", "true");
    try {
      switch (form.dataset.accountForm) {
        case "login": await login(values); break;
        case "password": await changePassword(values); break;
        case "register":
          await register(values);
          registrationMessage = "Đã đăng ký tài khoản. Vui lòng đăng nhập.";
          location.hash = "login";
          break;
      }
      form.reset();
    } catch (error) {
      if (error.name !== "AbortError") showError(form, sessionMessage(error), error.fields);
    } finally {
      for (const input of form.querySelectorAll('input[type="password"]')) input.value = "";
      submit.disabled = false;
      submit.textContent = label;
      form.setAttribute("aria-busy", "false");
    }
  });
}

void restoreSession();
