import { watchCart } from "./cart.js";

const badge = document.querySelector("[data-cart-count]");
watchCart(({ data }) => {
  const total = data?.totalItems ?? 0;
  badge.hidden = total === 0;
  badge.textContent = total > 99 ? "99+" : String(total);
  badge.closest("a").setAttribute("aria-label", total ? `Giỏ hàng, ${total} sản phẩm khả dụng` : "Giỏ hàng");
});
