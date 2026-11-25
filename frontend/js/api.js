import { API_BASE_URL } from "./config.js";

const fallbackMessages = {
  400: "Dữ liệu gửi đi không hợp lệ.",
  401: "Vui lòng đăng nhập để tiếp tục.",
  403: "Bạn không có quyền thực hiện thao tác này.",
  404: "Không tìm thấy thông tin yêu cầu.",
  409: "Dữ liệu đã thay đổi. Vui lòng kiểm tra lại.",
  413: "Dữ liệu tải lên vượt giới hạn.",
  415: "Định dạng dữ liệu không được hỗ trợ.",
  422: "Không thể xử lý dữ liệu đã gửi.",
  429: "Bạn đã gửi quá nhiều yêu cầu. Vui lòng thử lại sau."
};

let accessToken = null;

export class ApiError extends Error {
  constructor(message, { status = 0, code, fields = [], traceId = null }) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
    this.fields = fields;
    this.traceId = traceId;
  }
}

export function setAccessToken(token) {
  accessToken = typeof token === "string" && token ? token : null;
}

export function clearAccessToken() {
  accessToken = null;
}

export async function request(path, { method = "GET", body, query, auth = true, signal, expectedStatus } = {}) {
  const base = new URL(API_BASE_URL.replace(/\/+$/, "") + "/", window.location.href);
  const url = new URL(path.replace(/^\/+/, ""), base);
  if (url.origin !== base.origin || !url.pathname.startsWith(base.pathname)) {
    throw new TypeError("API path must stay within the configured base URL.");
  }
  const route = url.pathname.slice(base.pathname.length);
  const authRoute = route.startsWith("auth/");
  const cookieRequest = route === "auth/refresh" || route === "auth/logout";
  const anonymousAuth = ["auth/register", "auth/login", "auth/refresh"].includes(route);
  for (const [key, raw] of Object.entries(query ?? {})) {
    for (const value of Array.isArray(raw) ? raw : [raw]) {
      if (value != null) url.searchParams.append(key, value);
    }
  }

  const headers = new Headers({ Accept: "application/json" });
  if (auth && accessToken && !anonymousAuth) headers.set("Authorization", "Bearer " + accessToken);
  if (cookieRequest) headers.set("X-Requested-With", "XMLHttpRequest");
  let payload;
  if (body instanceof FormData) {
    if (cookieRequest) throw new TypeError("Refresh and logout require JSON.");
    payload = body;
  } else if (body !== undefined || cookieRequest) {
    headers.set("Content-Type", "application/json");
    payload = JSON.stringify(body ?? {});
  }

  let response;
  try {
    response = await fetch(url, {
      method, headers, body: payload, credentials: authRoute ? "include" : "omit",
      redirect: "error", signal
    });
  } catch (error) {
    if (signal?.aborted || error?.name === "AbortError") throw error;
    throw new ApiError("Không thể kết nối. Vui lòng kiểm tra mạng và thử lại.", { code: "NETWORK_ERROR" });
  }

  let document;
  if (response.headers.get("Content-Type")?.match(/\/(?:json|[^;\s]+\+json)(?:\s*;|$)/i)) {
    try {
      document = await response.json();
    } catch (error) {
      if (signal?.aborted || error?.name === "AbortError") throw error;
    }
  }
  if (!response.ok || document?.success === false) {
    const serverMessage = document?.success === false && typeof document.message === "string" ? document.message.trim() : "";
    const message = response.status >= 500 ? "Hệ thống đang gặp sự cố. Vui lòng thử lại sau."
      : serverMessage || fallbackMessages[response.status] || "Yêu cầu chưa hoàn tất. Vui lòng thử lại.";
    throw new ApiError(message, {
      status: response.status,
      code: typeof document?.errorCode === "string" && document.errorCode.trim()
        ? document.errorCode : "HTTP_" + response.status,
      fields: Array.isArray(document?.errors) ? document.errors.filter(item =>
        typeof item?.field === "string" && typeof item?.message === "string") : [],
      traceId: typeof document?.traceId === "string" ? document.traceId : null
    });
  }
  if ((expectedStatus !== undefined && response.status !== expectedStatus) ||
      document?.success !== true || !Object.hasOwn(document, "data")) {
    throw new ApiError("Phản hồi từ hệ thống không hợp lệ.", { status: response.status, code: "INVALID_RESPONSE" });
  }
  return document;
}
