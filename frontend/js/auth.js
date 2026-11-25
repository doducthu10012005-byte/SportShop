import { ApiError, clearAccessToken, request, setAccessToken } from "./api.js";

const markerKey = "sportshop.session";
const listeners = new Set();
let marker = readMarker() ?? null;
let state = { phase: "idle", user: null, message: "" };
let identity = null;
let epoch = 0;
let tokenVersion = 0;
let refreshFlight;
let logoutFlight;
let cookieQueue = Promise.resolve();

function readMarker() {
  try { return localStorage.getItem(markerKey); } catch { return undefined; }
}

function writeMarker(value) {
  marker = value;
  try {
    if (value) localStorage.setItem(markerKey, value);
    else localStorage.removeItem(markerKey);
  } catch {
    // The current tab still works when browser storage is unavailable.
  }
}

function withCookieLock(action) {
  if (navigator.locks) return navigator.locks.request("sportshop-session-cookie", action);
  const next = cookieQueue.then(action);
  cookieQueue = next.catch(() => {});
  return next;
}

function publish(phase, user = null, message = "") {
  state = Object.freeze({ phase, user: user ? Object.freeze(user) : null, message });
  for (const listener of listeners) listener(state);
}

export function watchSession(listener) {
  listeners.add(listener);
  listener(state);
  return () => listeners.delete(listener);
}

function clearSession(message = "", forget = true) {
  epoch++;
  identity = null;
  clearAccessToken();
  const stored = readMarker();
  if (forget && (stored === undefined || stored === marker)) writeMarker(null);
  publish("anonymous", null, message);
}

function syncMarker() {
  const stored = readMarker();
  if (stored === undefined || stored === marker) return;
  marker = stored;
  clearSession("", false);
  if (marker) void restoreSession();
}

window.addEventListener("storage", event => {
  if (event.key === markerKey || event.key === null) syncMarker();
});
window.addEventListener("pageshow", event => {
  if (event.persisted) syncMarker();
});

function assertCurrent(operation) {
  if (operation !== epoch) throw new DOMException("Phiên đã thay đổi.", "AbortError");
}

function checkMarker(expected) {
  const current = readMarker();
  if (marker !== expected || (current !== undefined && current !== expected))
    throw new DOMException("Phiên đã thay đổi.", "AbortError");
}

function post(path, body) {
  return request(path, { method: "POST", body, signal: AbortSignal.timeout(12000) });
}

function installSession(data) {
  const user = data?.user;
  if (typeof data?.accessToken !== "string" || !data.accessToken || !Number.isInteger(user?.userId) ||
      !["Customer", "Admin"].includes(user.role) || typeof user.mustChangePassword !== "boolean") {
    throw new ApiError("Phiên đăng nhập từ máy chủ không hợp lệ.", { code: "INVALID_RESPONSE" });
  }
  if (identity && (identity.userId !== user.userId || identity.role !== user.role))
    throw new ApiError("Tài khoản của phiên đã thay đổi.", { code: "INVALID_RESPONSE" });
  identity = { userId: user.userId, role: user.role, mustChangePassword: user.mustChangePassword };
  setAccessToken(data.accessToken);
  tokenVersion++;
  if (state.user) publish("authenticated", { ...state.user, mustChangePassword: user.mustChangePassword });
}

async function loadProfile(operation) {
  const { data } = await request("/users/me", { signal: AbortSignal.timeout(12000) });
  assertCurrent(operation);
  if (data?.userId !== identity?.userId || data?.role !== identity?.role)
    throw new ApiError("Không thể xác minh tài khoản của phiên.", { code: "INVALID_RESPONSE" });
  publish("authenticated", {
    userId: data.userId, role: data.role, fullName: data.fullName, email: data.email,
    phoneNumber: data.phoneNumber, mustChangePassword: identity.mustChangePassword
  });
}

function refreshSession() {
  const operation = epoch;
  if (refreshFlight?.operation === operation) return refreshFlight.promise;
  const expected = marker;
  const flight = { operation };
  refreshFlight = flight;
  flight.promise = withCookieLock(async () => {
    assertCurrent(operation);
    checkMarker(expected);
    const { data } = await post("/auth/refresh");
    assertCurrent(operation);
    checkMarker(expected);
    installSession(data);
  }).catch(error => {
    if (operation === epoch) {
      clearSession(error.status === 401 ? "Phiên đã hết hạn. Vui lòng đăng nhập lại." : sessionMessage(error));
    }
    throw error;
  }).finally(() => {
    if (refreshFlight === flight) refreshFlight = undefined;
  });
  return flight.promise;
}

let restoration;
export function restoreSession() {
  if (restoration?.operation === epoch) return restoration.promise;
  if (state.phase === "authenticated" || logoutFlight) return Promise.resolve();
  if (!marker) {
    if (state.phase === "idle") publish("anonymous");
    return Promise.resolve();
  }
  const operation = epoch;
  publish("checking");
  const attempt = { operation };
  restoration = attempt;
  attempt.promise = (async () => {
    try {
      await refreshSession();
      await loadProfile(operation);
    } catch (error) {
      if (operation === epoch) clearSession(sessionMessage(error));
    } finally {
      if (restoration === attempt) restoration = undefined;
    }
  })();
  return attempt.promise;
}

export function register(input) {
  return post("/auth/register", input);
}

export async function login(input) {
  const operation = ++epoch;
  identity = null;
  clearAccessToken();
  // Keep the login form visible while its submit button reports progress.
  try {
    await withCookieLock(async () => {
      assertCurrent(operation);
      const { data } = await post("/auth/login", input);
      assertCurrent(operation);
      installSession(data);
      writeMarker(crypto.randomUUID());
    });
    await loadProfile(operation);
  } catch (error) {
    if (operation === epoch && identity) clearSession(sessionMessage(error));
    throw error;
  }
}

function handleAccessError(error, operation, usedVersion) {
  assertCurrent(operation);
  if (error.code === "PASSWORD_CHANGE_REQUIRED" && identity) {
    identity.mustChangePassword = true;
    if (state.user) publish("authenticated", { ...state.user, mustChangePassword: true });
  } else if (["ACCOUNT_LOCKED", "ACCOUNT_INACTIVE"].includes(error.code) ||
      (error.status === 401 && tokenVersion === usedVersion)) {
    clearSession(sessionMessage(error));
  }
}

export async function authorizedRequest(path, options) {
  if (!identity || logoutFlight) throw new ApiError("Vui lòng đăng nhập để tiếp tục.", { status: 401, code: "UNAUTHORIZED" });
  const method = (options?.method ?? "GET").toUpperCase();
  const allowedDuringPasswordChange = (path === "/users/me" && method === "GET") ||
    (path === "/users/me/password" && method === "PUT");
  const operation = epoch;
  const firstVersion = tokenVersion;
  const send = async () => {
    options?.signal?.throwIfAborted();
    if (identity.mustChangePassword && !allowedDuringPasswordChange)
      throw new ApiError("Bạn cần đổi mật khẩu để tiếp tục.", { status: 403, code: "PASSWORD_CHANGE_REQUIRED" });
    const response = await request(path, options);
    assertCurrent(operation);
    return response;
  };
  try {
    return await send();
  } catch (error) {
    assertCurrent(operation);
    if (error.status !== 401 || error.code === "INVALID_CREDENTIALS") {
      handleAccessError(error, operation, firstVersion);
      throw error;
    }
    if (firstVersion === tokenVersion) await refreshSession();
    assertCurrent(operation);
    const retriedVersion = tokenVersion;
    try {
      return await send();
    } catch (retryError) {
      handleAccessError(retryError, operation, retriedVersion);
      throw retryError;
    }
  }
}

export function logout() {
  if (logoutFlight) return logoutFlight;
  if (!identity) return Promise.resolve();
  const operation = ++epoch;
  const expected = marker;
  publish("signing-out");
  logoutFlight = withCookieLock(async () => {
    assertCurrent(operation);
    checkMarker(expected);
    writeMarker(null);
    try {
      await post("/auth/logout");
    } catch (error) {
      if (error.status !== 401) throw error;
      // Refresh once inside this lock and use the result only to revoke the cookie.
      const { data } = await post("/auth/refresh");
      assertCurrent(operation);
      installSession(data);
      await post("/auth/logout");
    }
  }).then(() => {
    if (operation === epoch) clearSession("Đã đăng xuất.");
  }).catch(() => {
    if (operation === epoch) clearSession("Đã thoát trên thiết bị này. Chưa xác nhận được với máy chủ.");
  }).finally(() => { logoutFlight = undefined; });
  return logoutFlight;
}

export async function changePassword(input) {
  const operation = epoch;
  await authorizedRequest("/users/me/password", { method: "PUT", body: input, signal: AbortSignal.timeout(12000) });
  assertCurrent(operation);
  clearSession("Đã đổi mật khẩu. Vui lòng đăng nhập lại.");
}

export function sessionMessage(error) {
  if (error?.name === "TimeoutError") return "Máy chủ phản hồi chậm. Vui lòng thử lại.";
  if (["ACCOUNT_LOCKED", "ACCOUNT_INACTIVE"].includes(error?.code)) return "Tài khoản đã bị khóa.";
  return error instanceof ApiError ? error.message : "Yêu cầu chưa hoàn tất. Vui lòng thử lại.";
}
