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
@app.post("/detect_capture")
async def detect_capture(request: CaptureRequest):

    try:
        # Decode base64 → numpy image
        img_bytes = base64.b64decode(request.image_base64)
        nparr = np.frombuffer(img_bytes, np.uint8)
        img = cv2.imdecode(nparr, cv2.IMREAD_COLOR)

        if img is None:
            return JSONResponse(
                status_code=400,
                content={"error": "Invalid image"}
            )

        # =============================
        # UNDISTORT (giữ nguyên như bạn đang dùng)
        # =============================
        if camera_matrix is not None:
            img = cv2.undistort(img, camera_matrix, dist_coeffs)

        # =============================
        # YOLO INFERENCE
        # =============================
        results = model.predict(
            img,
            imgsz=2560,
            conf=0.4,
            verbose=False
        )[0]

        ng_count = 0
        ok_count = 0

        for box in results.boxes:
            cls = int(box.cls[0])
            x1, y1, x2, y2 = map(int, box.xyxy[0])

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
            h, w = img.shape[:2]

            newcameramtx, roi = cv2.getOptimalNewCameraMatrix(
                camera_matrix,
                dist_coeffs,
                (w, h),
                1,
                (w, h)
            )

            img = cv2.undistort(
                img,
                camera_matrix,
                dist_coeffs,
                None,
                newcameramtx
            )

            x, y, w, h = roi
            img = img[y:y+h, x:x+w]

        # =============================
        # YOLO INFERENCE
        # =============================
        results = model.predict(
            img,
            imgsz=2560,
            conf=0.4,
            verbose=False
        )[0]

        ng_count = 0
        ok_count = 0

        for box in results.boxes:
            cls = int(box.cls[0])
            x1, y1, x2, y2 = map(int, box.xyxy[0])

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
            "image_base64": img_base64
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