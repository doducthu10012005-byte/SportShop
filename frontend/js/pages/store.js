import "../ui.js";
import { restoreSession, watchSession } from "../auth.js";

const accountLink = document.querySelector("[data-account-link]");
watchSession(({ phase, user }) => {
  if (user?.mustChangePassword) {
    window.location.replace("./account/#password");
    return;
  }
  accountLink.textContent = phase === "anonymous" ? "Đăng nhập" : "Tài khoản";
});
void restoreSession();

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
