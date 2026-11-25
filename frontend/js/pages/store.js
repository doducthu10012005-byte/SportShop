import "../ui.js";
import "../store-session.js";
import "../cart-badge.js";
import { catalogError, getCatalog, renderProducts, setCatalogState } from "../catalog.js";

const featuredState = document.querySelector("[data-featured-state]");
const featuredGrid = document.querySelector("[data-featured-products]");
async function loadFeatured() {
  setCatalogState(featuredState, "loading", "Đang tải sản phẩm");
  try {
    const { items } = await getCatalog("/products", { query: { sort: "newest", page: 1, pageSize: 4 } });
    renderProducts(featuredGrid, items);
    if (items.length) setCatalogState(featuredState);
    else setCatalogState(featuredState, "empty", "Chưa có sản phẩm", "Sản phẩm mới sẽ xuất hiện ở đây khi có.");
  } catch (error) {
    setCatalogState(featuredState, "error", "Chưa tải được sản phẩm", catalogError(error), true);
  }
}
featuredState.querySelector("[data-retry]").addEventListener("click", loadFeatured);
void loadFeatured();

const guide = document.querySelector("#sport-guide");
const title = guide.querySelector("[data-guide-title]");
const description = guide.querySelector("[data-guide-description]");

const sports = {
  running: ["Chạy bộ", "Bắt đầu với giày phù hợp quãng đường, trang phục thoáng khí và các phụ kiện giúp bạn thoải mái qua từng bước chạy."],
  training: ["Tập luyện", "Chọn dụng cụ theo không gian và bài tập của bạn. Thảm tập, tạ tay và dây kháng lực là những lựa chọn dễ bắt đầu."],
  racket: ["Thể thao sân vợt", "Chú ý trọng lượng vợt, cỡ tay cầm và giày phù hợp với mặt sân bạn thường chơi."]
};

for (const button of document.querySelectorAll("[data-sport]")) {
  button.addEventListener("click", () => {
    [title.textContent, description.textContent] = sports[button.dataset.sport];
    guide.showModal();
  });
}
