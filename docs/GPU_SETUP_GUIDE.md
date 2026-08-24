# 🚀 CẨM NANG SETUP SERVER AI GPU (1 CLICK - TESTED 100%)

Tài liệu này đã được **kiểm nghiệm thực tế 100% trên card NVIDIA RTX 5060 Ti / RTX 3090 (Ubuntu 24.04)**.

---

## 🌟 BƯỚC 0: CHUẨN BỊ (CHỈ LÀM 1 LẦN NẾU DÙNG FLUX)
* Nếu chọn chạy **FLUX (Người Thật)**: Vào link [huggingface.co/black-forest-labs/FLUX.1-schnell](https://huggingface.co/black-forest-labs/FLUX.1-schnell) bấm nút **"Agree and access repository"** (Miễn phí 100%).
* Nếu chọn chạy **Animagine-XL (Anime 2D)**: Không cần chuẩn bị gì cả, chạy ngay!

---

## ⚡ 1. CHỌN 1 TRONG 2 LỆNH ALL-IN-ONE (DÁN VÀO POWERSHELL LÀ XONG)

### 👑 **TÙY CHỌN A: CHẠY FLUX.1 (NGƯỜI THẬT SIÊU THỰC 8K - PHOTOREALISTIC)**

```bash
sudo apt update && sudo apt install -y python3-pip wget curl psmisc
wget -q https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-amd64.deb && sudo dpkg -i cloudflared-linux-amd64.deb
pip3 install --upgrade --break-system-packages torch torchvision --index-url https://download.pytorch.org/whl/cu129
pip3 install --break-system-packages diffusers transformers accelerate sentencepiece protobuf fastapi uvicorn pydantic

killall -9 cloudflared python3 2>/dev/null; fuser -k 8000/tcp 2>/dev/null

python3 -c '
code = """import io, base64, torch, uvicorn
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from diffusers import FluxPipeline

app = FastAPI(title="Dedicated FLUX Server")
print("⏳ Đang nạp model FLUX.1-schnell vào GPU...")
pipe = FluxPipeline.from_pretrained(
    "black-forest-labs/FLUX.1-schnell",
    torch_dtype=torch.bfloat16,
    token="YOUR_HUGGINGFACE_TOKEN_HERE" # Thay bằng token của bạn từ https://huggingface.co/settings/tokens
).to("cuda")
print("✅ FLUX ĐÃ SẴN SÀNG 100%!")

class ImageRequest(BaseModel):
    prompt: str
    width: int = 1024
    height: int = 1024
    num_inference_steps: int = 4
    guidance_scale: float = 0.0
    seed: int = -1

@app.get("/")
def root():
    return {"status": "ok", "model": "FLUX.1-schnell"}

@app.post("/generate")
def generate_image(req: ImageRequest):
    try:
        generator = torch.Generator("cuda").manual_seed(req.seed) if req.seed > 0 else None
        image = pipe(
            prompt=req.prompt,
            width=req.width,
            height=req.height,
            num_inference_steps=req.num_inference_steps,
            guidance_scale=req.guidance_scale,
            generator=generator
        ).images[0]
        buf = io.BytesIO()
        image.save(buf, format="JPEG", quality=95)
        return {"image": f"data:image/jpeg;base64,{base64.b64encode(buf.getvalue()).decode()}"}
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8000)
"""
with open("server.py", "w") as f:
    f.write(code)
'

python3 server.py &
sleep 20
cloudflared tunnel --edge-ip-version 4 --url http://127.0.0.1:8000
```

---

### 🎨 **TÙY CHỌN B: CHẠY ANIMAGINE-XL (ANIME 2D / MANHWA VISUAL NOVEL)**

```bash
sudo apt update && sudo apt install -y python3-pip wget curl psmisc
wget -q https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-amd64.deb && sudo dpkg -i cloudflared-linux-amd64.deb
pip3 install --upgrade --break-system-packages torch torchvision --index-url https://download.pytorch.org/whl/cu129
pip3 install --break-system-packages diffusers transformers accelerate sentencepiece protobuf fastapi uvicorn pydantic

killall -9 cloudflared python3 2>/dev/null; fuser -k 8000/tcp 2>/dev/null

python3 -c '
code = """import io, base64, torch, uvicorn
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from diffusers import AutoPipelineForText2Image

app = FastAPI(title="Dedicated Anime Server")
print("⏳ Đang nạp Animagine-XL vào GPU...")
pipe = AutoPipelineForText2Image.from_pretrained(
    "cagliostrolab/animagine-xl-3.1",
    torch_dtype=torch.float16,
    use_safetensors=True
).to("cuda")
print("✅ ANIMAGINE-XL ĐÃ SẴN SÀNG 100%!")

class ImageRequest(BaseModel):
    prompt: str
    width: int = 1024
    height: int = 1024
    num_inference_steps: int = 25
    guidance_scale: float = 7.0
    seed: int = -1

@app.get("/")
def root():
    return {"status": "ok", "model": "Animagine-XL-3.1"}

@app.post("/generate")
def generate_image(req: ImageRequest):
    try:
        generator = torch.Generator("cuda").manual_seed(req.seed) if req.seed > 0 else None
        image = pipe(
            prompt=req.prompt,
            negative_prompt="nsfw, lowres, bad anatomy, bad hands, text, error, missing fingers, cropped, worst quality, low quality, blurry",
            width=req.width,
            height=req.height,
            num_inference_steps=req.num_inference_steps,
            guidance_scale=req.guidance_scale,
            generator=generator
        ).images[0]
        buf = io.BytesIO()
        image.save(buf, format="JPEG", quality=95)
        return {"image": f"data:image/jpeg;base64,{base64.b64encode(buf.getvalue()).decode()}"}
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8000)
"""
with open("server.py", "w") as f:
    f.write(code)
'

python3 server.py &
sleep 15
cloudflared tunnel --edge-ip-version 4 --url http://127.0.0.1:8000
```

---

## 🔌 2. KẾT NỐI VÀO DỰ ÁN TRÊN MÁY TÍNH
1. Sau khi chạy, Cloudflare sẽ in ra đường link màu xanh:
   👉 `https://xxxx.trycloudflare.com`
2. Bạn mở file `BE/appsettings.Development.json` trên máy tính và dán đường link vào:
   ```json
   "AiProviders": {
     "ImageProvider": "Dedicated",
     "DedicatedServerUrl": "https://xxxx.trycloudflare.com"
   }
   ```
3. **Hoàn tất!** Dự án của bạn đã nối trực tiếp với GPU Cloud!
