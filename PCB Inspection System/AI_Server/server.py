from fastapi import FastAPI, File, UploadFile
from fastapi.responses import JSONResponse
import uvicorn
import cv2
import numpy as np
import base64
import os
import psutil 
import signal  
from ultralytics import YOLO
from pydantic import BaseModel
class CaptureRequest(BaseModel):
    image_base64: str
# =============================
# CONFIG
# =============================
MODEL_PATH = "best.pt"
CAMERA_PARAM_PATH = "camera_params.npz"

NG_CLASS_INDEX = 0
OK_CLASS_INDEX = 1
IMGZ=1280
CONF=0.4
IOU = 0.3
# =============================
# INIT
# =============================
app = FastAPI()

print("Loading YOLO model...")
model = YOLO(MODEL_PATH)
print("Model loaded!")

# Load camera calibration if exists
camera_matrix = None
dist_coeffs = None

if os.path.exists(CAMERA_PARAM_PATH):
    data = np.load(CAMERA_PARAM_PATH)
    camera_matrix = data["camera_matrix"]
    dist_coeffs = data["dist_coeffs"]
    print("Camera calibration loaded.")
else:
    print("WARNING: camera_params.npz not found. Running without undistort.")

def kill_port(port):
    """Hàm tìm và xóa process đang dùng port 8000"""
    for proc in psutil.process_iter(['pid', 'name']):
        try:
            for conn in proc.connections(kind='inet'):
                if conn.laddr.port == port:
                    print(f"Killing PID {proc.info['pid']} đang chiếm dụng cổng {port}...")
                    proc.terminate() # Hoặc proc.kill()
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue

@app.post("/reload_model")
async def reload_model():
    """Endpoint để C# gọi khi muốn load lại model mới"""
    global model
    try:
        model = YOLO(MODEL_PATH)
        return {"status": "success", "message": "Model reloaded"}
    except Exception as e:
        return JSONResponse(status_code=500, content={"error": str(e)})

@app.post("/shutdown")
async def shutdown():
    """Endpoint để C# gọi khi đóng Form"""
    print("Shutting down server...")
    os.kill(os.getpid(), signal.SIGTERM)
    return {"message": "Server stopped"}            
# =============================
# HELPER
# =============================
def image_to_base64(img):
    _, buffer = cv2.imencode(".jpg", img)
    return base64.b64encode(buffer).decode("utf-8")
@app.post("/detect_lr")
async def detect_lr(file: UploadFile = File(...)):

    try:

        # =============================
        # READ IMAGE
        # =============================

        contents = await file.read()
        nparr = np.frombuffer(contents, np.uint8)
        img = cv2.imdecode(nparr, cv2.IMREAD_COLOR)

        if img is None:
            return JSONResponse(
                status_code=400,
                content={"error": "Invalid image"}
            )

        # =============================
        # UNDISTORT
        # =============================

        if camera_matrix is not None:
            img = cv2.undistort(img, camera_matrix, dist_coeffs)

        drawing_img = img.copy()

        h, w = drawing_img.shape[:2]

        center_line = w // 2

        # =============================
        # YOLO INFERENCE
        # =============================

        results = model.predict(
            source=drawing_img,
            conf=CONF,
            imgsz=IMGZ,
            verbose=False,
            agnostic_nms=True,
            iou=0.3
        )[0]

        # =============================
        # COUNT
        # =============================

        left_ok = 0
        left_ng = 0
        right_ok = 0
        right_ng = 0
        label_lines = []
        # =============================
        # LOOP DETECTION
        # =============================
        
        for box in results.boxes:

            cls = int(box.cls[0])

            x1, y1, x2, y2 = box.xyxy[0]
            bw = (x2 - x1) / w
            bh = (y2 - y1) / h
            cx = int((x1 + x2) / 2)
            cy = int((y1 + y2) / 2)
            label_lines.append(f"{cls} {cx:.6f} {cy:.6f} {bw:.6f} {bh:.6f}")
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

        # =============================
        # DRAW CENTER LINE
        # =============================

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

        # =============================
        # HEADER
        # =============================

        header_h = int(h * 0.1)

        header = np.zeros((header_h, w, 3), dtype=np.uint8)

        font_scale = h / 1000

        text = f"                             Left:{left_ok}/{left_ng}                                    Right:{right_ok}/{right_ng}"

        cv2.putText(
            header,
            text,
            (20, int(header_h * 0.8)),
            cv2.FONT_HERSHEY_SIMPLEX,
            font_scale,
            (0,255,255),
            2
        )

        final_img = np.vstack((header, drawing_img))

        # =============================
        # IMAGE -> BASE64
        # =============================

        img_base64 = image_to_base64(final_img)

        # =============================
        # RETURN RESULT
        # =============================

        return JSONResponse({

            "left_ok": left_ok,
            "left_ng": left_ng,
            "right_ok": right_ok,
            "right_ng": right_ng,
            "label_lines": label_lines,
            "image_base64": img_base64
        })

    except Exception as e:

        print("ERROR:", str(e))

        return JSONResponse(
            status_code=500,
            content={"error": str(e)}
        )

# =============================
# API ROUTE
# =============================
@app.post("/detect")
async def detect(file: UploadFile = File(...)):

    try:
        contents = await file.read()
        nparr = np.frombuffer(contents, np.uint8)
        img = cv2.imdecode(nparr, cv2.IMREAD_COLOR)

        if img is None:
            return JSONResponse(
                status_code=400,
                content={"error": "Invalid image"}
            )

        # =============================
        # UNDISTORT HERE
        # =============================
        if camera_matrix is not None:
            img = cv2.undistort(img, camera_matrix, dist_coeffs)

        # =============================
        # YOLO INFERENCE
        # =============================
        results = model.predict(
            source=img,
            imgsz=1280,
            conf=CONF,
            iou=IOU,
            agnostic_nms=True,
            verbose=False
        )[0]

        ng_count = 0
        ok_count = 0
        h, w = img.shape[:2]
        label_lines = []
        for box in results.boxes:
            cls = int(box.cls[0])
            x1, y1, x2, y2 = map(int, box.xyxy[0])

            cx = ((x1 + x2) / 2) / w
            cy = ((y1 + y2) / 2) / h
            bw = (x2 - x1) / w
            bh = (y2 - y1) / h

            label_lines.append(f"{cls} {cx:.6f} {cy:.6f} {bw:.6f} {bh:.6f}")

            if cls == NG_CLASS_INDEX:
                color = (0, 0, 255)
                ng_count += 1
            else:
                color = (0, 255, 0)
                ok_count += 1

            cv2.rectangle(img, (x1, y1), (x2, y2), color, 2)

        img_base64 = image_to_base64(img)

        return JSONResponse({
            "ok_count": ok_count,
            "ng_count": ng_count,
            "image_base64": img_base64,
            "label_lines": label_lines
        })

    except Exception as e:
        print("ERROR:", str(e))
        return JSONResponse(
            status_code=500,
            content={"error": str(e)}
        )

# =============================
# MAIN
# =============================
if __name__ == "__main__":
    kill_port(8000)
    uvicorn.run(app, host="127.0.0.1", port=8000)