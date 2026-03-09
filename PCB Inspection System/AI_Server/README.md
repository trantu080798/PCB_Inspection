Bước 1: Tạo folder riêng cho AI Server
AI_Server   - best.pt
            - server.py
            - camera_params.npz
            - Readme
Bước 2: viết code server.py
Bước 3: Cài thư viện cần thiết
    - pip install opencv-python  
    - pip install ultralytics 
    - pip install fastapi uvicorn opencv-python ultralytics numpy
    - pip install python-multipart  
    - pip install psutil

chạy thử nghiệm
kết quả
    Loading YOLO model...
    Model loaded!
    Uvicorn running on http://127.0.0.1:8000

mở trình duyệt: http://127.0.0.1:8000/docs 