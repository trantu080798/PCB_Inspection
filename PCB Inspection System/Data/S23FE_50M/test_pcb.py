import cv2
import os
import numpy as np
import time
from ultralytics import YOLO

# ==========================================
# --- CẤU HÌNH HỆ THỐNG ---
# ==========================================
MODEL_PATH = 'best.pt'
IMAGE_FOLDER = '.'
DATASET_PATH = 'collected_data'
NG_CLASS_INDEX = 0
OK_CLASS_INDEX = 1
IMG_SIZE_INFERENCE = 1280

# ==========================================
# --- 1. KHỞI TẠO ---
# ==========================================
model = YOLO(MODEL_PATH)

camera_matrix = None
dist_coeffs = None
if os.path.exists("camera_params.npz"):
    data = np.load("camera_params.npz")
    camera_matrix = data["camera_matrix"]
    dist_coeffs = data["dist_coeffs"]
    print("✅ Đã load thông số Camera Calib.")

valid_extensions = ('.jpg', '.jpeg', '.png', '.bmp', '.webp')
images = [f for f in os.listdir(IMAGE_FOLDER)
          if f.lower().endswith(valid_extensions) and f != 'best.pt']
images.sort()

current_idx = 0
mode = "GALLERY"
is_live_viewing = False
cap = None
needs_update = True
cached_display_img = None
last_conf_val = -1
last_processed_idx = -1

last_raw_frame = None
last_results = None

def nothing(x):
    global needs_update
    needs_update = True

# ==========================================
# --- 2. LƯU FULL FRAME DATA ---
# ==========================================
def save_full_frame_data(frame, results):
    if frame is None or results is None:
        print("❌ Không có dữ liệu để lưu.")
        return

    img_dir = os.path.join(DATASET_PATH, 'images')
    lbl_dir = os.path.join(DATASET_PATH, 'labels')
    os.makedirs(img_dir, exist_ok=True)
    os.makedirs(lbl_dir, exist_ok=True)

    timestamp = int(time.time())
    file_name = f"pcb_{timestamp}"

    img_path = os.path.join(img_dir, f"{file_name}.jpg")
    lbl_path = os.path.join(lbl_dir, f"{file_name}.txt")

    h, w = frame.shape[:2]
    label_lines = []

    for box in results.boxes:
        cls = int(box.cls[0])
        x1, y1, x2, y2 = box.xyxy[0].tolist()

        cx = ((x1 + x2) / 2) / w
        cy = ((y1 + y2) / 2) / h
        bw = (x2 - x1) / w
        bh = (y2 - y1) / h

        label_lines.append(f"{cls} {cx:.6f} {cy:.6f} {bw:.6f} {bh:.6f}")

    cv2.imwrite(img_path, frame)

    with open(lbl_path, 'w') as f:
        f.write("\n".join(label_lines))

    print(f"✅ Đã lưu full frame: {file_name} ({len(label_lines)} objects)")

# ==========================================
# --- 3. DỰ ĐOÁN VÀ VẼ ---
# ==========================================
def process_and_draw(img, conf_val, title_name):
    global last_raw_frame, last_results

    last_raw_frame = img.copy()
    drawing_img = img.copy()
    h, w = drawing_img.shape[:2]

    results = model.predict(
        source=drawing_img,
        conf=conf_val,
        imgsz=IMG_SIZE_INFERENCE,
        verbose=False,
        agnostic_nms=True,
        iou=0.2
    )[0]

    last_results = results

    ng_count = 0
    ok_count = 0

    for box in results.boxes:
        cls = int(box.cls[0])
        x1, y1, x2, y2 = map(int, box.xyxy[0])

        color = (0, 0, 255) if cls == NG_CLASS_INDEX else (0, 255, 0)
        thickness = 4 if cls == NG_CLASS_INDEX else 2

        cv2.rectangle(drawing_img, (x1, y1), (x2, y2), color, thickness)

        if cls == NG_CLASS_INDEX:
            ng_count += 1
        else:
            ok_count += 1

    header_h = int(h * 0.1)
    header_bg = np.zeros((header_h, w, 3), dtype=np.uint8)

    font_scale = h / 1000 * 1.0
    p_text = f"MODE: {mode} | {title_name}"
    r_text = f"NG: {ng_count}  OK: {ok_count} | Conf: {conf_val}"

    cv2.putText(header_bg, p_text, (20, int(header_h * 0.4)),
                cv2.FONT_HERSHEY_SIMPLEX, font_scale, (255, 255, 255), 2)

    cv2.putText(header_bg, r_text, (20, int(header_h * 0.8)),
                cv2.FONT_HERSHEY_SIMPLEX, font_scale, (0, 255, 255), 2)

    return np.vstack((header_bg, drawing_img))

# ==========================================
# --- 4. MAIN LOOP ---
# ==========================================
cv2.namedWindow("PCB Inspection System", cv2.WINDOW_NORMAL)
cv2.createTrackbar("Conf", "PCB Inspection System", 4, 9, nothing)

current_frame = None

while True:
    val = cv2.getTrackbarPos("Conf", "PCB Inspection System")
    current_conf = max(0.1, val / 10.0)

    if mode == "GALLERY":
        if cap is not None:
            cap.release()
            cap = None

        if images and (needs_update or current_idx != last_processed_idx or current_conf != last_conf_val):
            img_path = os.path.join(IMAGE_FOLDER, images[current_idx])
            original_img = cv2.imread(img_path)

            if original_img is not None:
                cached_display_img = process_and_draw(
                    original_img, current_conf, images[current_idx])
                last_processed_idx = current_idx
                last_conf_val = current_conf
                needs_update = False

    elif mode == "LIVE":
        if cap is None:
            cap = cv2.VideoCapture(0, cv2.CAP_DSHOW)
            cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*'MJPG'))
            cap.set(cv2.CAP_PROP_FRAME_WIDTH, 1280)
            cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 720)
            cap.set(cv2.CAP_PROP_AUTOFOCUS, 0)
            is_live_viewing = True

        if is_live_viewing:
            ret, frame = cap.read()
            if ret:
                # if camera_matrix is not None:
                #     frame = cv2.undistort(frame, camera_matrix, dist_coeffs)

                current_frame = frame.copy()
                display_live = frame.copy()

                cv2.putText(display_live,
                            "LIVE - Press 'C' to Capture & Inspect",
                            (20, 50),
                            cv2.FONT_HERSHEY_SIMPLEX,
                            1, (0, 255, 255), 2)

                cv2.imshow("PCB Inspection System", display_live)

    if not (mode == "LIVE" and is_live_viewing) and cached_display_img is not None:
        cv2.imshow("PCB Inspection System", cached_display_img)

    key = cv2.waitKey(1) & 0xFF

    if key == ord('n'):
        mode = "GALLERY"
        current_idx = (current_idx + 1) % len(images)
        needs_update = True

    elif key == ord('p'):
        mode = "GALLERY"
        current_idx = (current_idx - 1) % len(images)
        needs_update = True

    elif key == ord('a'):
        mode = "LIVE"
        is_live_viewing = True

    elif key == ord('l'):
        if mode == "LIVE":
            is_live_viewing = True
            print("--> Trở lại Live stream")

    elif key == ord('c') and mode == "LIVE":
        if is_live_viewing and current_frame is not None:
            print("📸 Đang phân tích ảnh chụp...")
            cached_display_img = process_and_draw(
                current_frame, current_conf, "Live Capture")
            is_live_viewing = False

    elif key == ord('g'):
        print("💾 Đang lưu full frame để huấn luyện...")
        save_full_frame_data(last_raw_frame, last_results)

    elif key == ord('q') or key == 27:
        break

if cap is not None:
    cap.release()

cv2.destroyAllWindows()