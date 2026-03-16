import cv2
import os
import numpy as np
import time
from ultralytics import YOLO

# ==========================================
# CONFIG
# ==========================================
MODEL_PATH = "best.pt"
IMAGE_FOLDER = "."
DATASET_PATH = "collected_data"

NG_CLASS_INDEX = 0
OK_CLASS_INDEX = 1

IMG_SIZE_INFERENCE = 1280

ROWS = 6
COLS = 16

# ==========================================
# INIT MODEL
# ==========================================
model = YOLO(MODEL_PATH)

camera_matrix = None
dist_coeffs = None

if os.path.exists("camera_params.npz"):
    data = np.load("camera_params.npz")
    camera_matrix = data["camera_matrix"]
    dist_coeffs = data["dist_coeffs"]
    print("Loaded camera calibration")

# ==========================================
# IMAGE LIST
# ==========================================
valid_extensions = ('.jpg', '.jpeg', '.png', '.bmp', '.webp')

images = [
    f for f in os.listdir(IMAGE_FOLDER)
    if f.lower().endswith(valid_extensions) and f != "best.pt"
]

images.sort()

# ==========================================
# GLOBAL STATE
# ==========================================
current_idx = 0
mode = "GALLERY"

cap = None
is_live_viewing = False

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
# SAVE DATASET
# ==========================================
def save_full_frame_data(frame, results):

    if frame is None or results is None:
        print("No data to save")
        return

    img_dir = os.path.join(DATASET_PATH, "images")
    lbl_dir = os.path.join(DATASET_PATH, "labels")

    os.makedirs(img_dir, exist_ok=True)
    os.makedirs(lbl_dir, exist_ok=True)

    timestamp = int(time.time())
    name = f"pcb_{timestamp}"

    img_path = os.path.join(img_dir, name + ".jpg")
    lbl_path = os.path.join(lbl_dir, name + ".txt")

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

    with open(lbl_path, "w") as f:
        f.write("\n".join(label_lines))

    print("Saved dataset:", name)


# ==========================================
# PROCESS
# ==========================================
def process_and_draw(img, conf_val, title_name):

    global last_raw_frame, last_results

    last_raw_frame = img.copy()

    drawing_img = img.copy()

    h, w = drawing_img.shape[:2]

    center_line = w // 2

    # =====================================
    # YOLO DETECT
    # =====================================

    results = model.predict(
        source=drawing_img,
        conf=conf_val,
        imgsz=IMG_SIZE_INFERENCE,
        verbose=False,
        agnostic_nms=True,
        iou=0.3
    )[0]

    last_results = results

    # =====================================
    # COUNT
    # =====================================

    left_ok = 0
    left_ng = 0

    right_ok = 0
    right_ng = 0

    # =====================================
    # LOOP DETECTION
    # =====================================

    for box in results.boxes:

        cls = int(box.cls[0])

        x1, y1, x2, y2 = box.xyxy[0]

        cx = int((x1 + x2) / 2)
        cy = int((y1 + y2) / 2)

        # LEFT / RIGHT
        if cx < center_line:

            if cls == NG_CLASS_INDEX:
                left_ng += 1
                color = (0,0,255)
            else:
                left_ok += 1
                color = (0,255,0)

        else:

            if cls == NG_CLASS_INDEX:
                right_ng += 1
                color = (0,0,255)
            else:
                right_ok += 1
                color = (0,255,0)

        # DRAW BOX
        cv2.rectangle(
            drawing_img,
            (int(x1), int(y1)),
            (int(x2), int(y2)),
            color,
            2
        )

        # DRAW CENTER
        cv2.circle(
            drawing_img,
            (cx, cy),
            4,
            (255,255,0),
            -1
        )

    # =====================================
    # DRAW CENTER LINE (guide for operator)
    # =====================================

    cv2.line(
        drawing_img,
        (center_line, 0),
        (center_line, h),
        (255,0,0),
        4
    )

    cv2.putText(
        drawing_img,
        "CENTER",
        (center_line - 80, 50),
        cv2.FONT_HERSHEY_SIMPLEX,
        1,
        (255,0,0),
        3
    )

    # =====================================
    # HEADER
    # =====================================

    header_h = int(h * 0.1)

    header = np.zeros((header_h, w, 3), dtype=np.uint8)

    font_scale = h / 1000

    text1 = f"MODE: {mode} | {title_name}"

    text2 = f"LEFT  OK:{left_ok} NG:{left_ng}   |   RIGHT OK:{right_ok} NG:{right_ng}"

    cv2.putText(
        header,
        text1,
        (20, int(header_h * 0.4)),
        cv2.FONT_HERSHEY_SIMPLEX,
        font_scale,
        (255,255,255),
        2
    )

    cv2.putText(
        header,
        text2,
        (20, int(header_h * 0.8)),
        cv2.FONT_HERSHEY_SIMPLEX,
        font_scale,
        (0,255,255),
        2
    )

    final_img = np.vstack((header, drawing_img))

    return final_img


# ==========================================
# MAIN LOOP
# ==========================================
cv2.namedWindow("PCB Inspection System", cv2.WINDOW_NORMAL)

cv2.createTrackbar("Conf", "PCB Inspection System", 4, 9, nothing)

current_frame = None

while True:

    val = cv2.getTrackbarPos("Conf", "PCB Inspection System")
    current_conf = max(0.1, val / 10)

    if mode == "GALLERY":

        if cap is not None:
            cap.release()
            cap = None

        if images and (
            needs_update
            or current_idx != last_processed_idx
            or current_conf != last_conf_val
        ):

            img_path = os.path.join(IMAGE_FOLDER, images[current_idx])
            original_img = cv2.imread(img_path)

            if original_img is not None:

                cached_display_img = process_and_draw(
                    original_img,
                    current_conf,
                    images[current_idx]
                )

                last_processed_idx = current_idx
                last_conf_val = current_conf
                needs_update = False

    elif mode == "LIVE":

        if cap is None:

            cap = cv2.VideoCapture(0, cv2.CAP_DSHOW)

            cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*"MJPG"))
            cap.set(cv2.CAP_PROP_FRAME_WIDTH, 1280)
            cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 720)
            cap.set(cv2.CAP_PROP_AUTOFOCUS, 0)

            is_live_viewing = True

        if is_live_viewing:

            ret, frame = cap.read()

            if ret:

                if camera_matrix is not None:
                    frame = cv2.undistort(frame, camera_matrix, dist_coeffs)

                current_frame = frame.copy()

                display_live = frame.copy()

                center_line = 1280 // 2
                h = 720
                # luôn vẽ line trước
                offset = 40

                cv2.line(display_live,(center_line-offset,0),(center_line-offset,h),(0,0,255),2)
                cv2.line(display_live,(center_line+offset,0),(center_line+offset,h),(0,0,255),2)
                cv2.putText(
                    display_live,
                    "LIVE - Press C to Capture",
                    (20,50),
                    cv2.FONT_HERSHEY_SIMPLEX,
                    1,
                    (0,255,255),
                    2
                )

                cv2.imshow("PCB Inspection System", display_live)

    if not (mode == "LIVE" and is_live_viewing) and cached_display_img is not None:
        cv2.imshow("PCB Inspection System", cached_display_img)

    key = cv2.waitKey(1) & 0xFF

    if key == ord("n"):
        mode = "GALLERY"
        current_idx = (current_idx + 1) % len(images)
        needs_update = True

    elif key == ord("p"):
        mode = "GALLERY"
        current_idx = (current_idx - 1) % len(images)
        needs_update = True

    elif key == ord("a"):
        mode = "LIVE"
        is_live_viewing = True

    elif key == ord("c") and mode == "LIVE":

        if is_live_viewing and current_frame is not None:

            cached_display_img = process_and_draw(
                current_frame,
                current_conf,
                "Live Capture"
            )

            is_live_viewing = False

    elif key == ord("g"):

        save_full_frame_data(last_raw_frame, last_results)

    elif key == ord("q") or key == 27:
        break


if cap is not None:
    cap.release()

cv2.destroyAllWindows()

