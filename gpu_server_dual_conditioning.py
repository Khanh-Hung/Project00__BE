"""
=============================================================================
Project00 — Dual-Reference GPU Conditioning Server (PR #11 Reference)
Architecture:
  - Base Engine: SDXL (Animagine-XL 3.1 / SDXL 1.0)
  - Dual IP-Adapter Conditioning:
      Slot 1: Identity Anchor (Canonical Master Reference) -> scale ~0.60-0.70
      Slot 2: Scene Background Frame (Revision N-1 Artifact) -> scale ~0.15-0.25
  - Deterministic Text Prompt: Guides Outfit, Pose, Action, and Dynamic Events
=============================================================================
"""

import os
import torch
import base64
from io import BytesIO
from typing import Optional, List
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from PIL import Image
import requests
from diffusers import AutopipelineForText2Image, EulerAncestralDiscreteScheduler

app = FastAPI(title="Project00 Dedicated GPU Conditioning Engine", version="2.0.0")

# ---------------------------------------------------------------------------
# 1. Pipeline Initialization
# ---------------------------------------------------------------------------
MODEL_ID = "cagliostrolab/animagine-xl-3.1"
IP_ADAPTER_REPO = "h94/IP-Adapter"

print(f"Loading Base SDXL Pipeline: {MODEL_ID} on CUDA...")
pipe = AutoPipelineForText2Image.from_pretrained(
    MODEL_ID,
    torch_dtype=torch.float16,
    use_safetensors=True
).to("cuda")

pipe.scheduler = EulerAncestralDiscreteScheduler.from_config(pipe.scheduler.config)

# Load Dual IP-Adapter Weights (Slot 1 = Identity, Slot 2 = Scene)
print("Loading Dual-Slot IP-Adapter weights...")
pipe.load_ip_adapter(
    IP_ADAPTER_REPO,
    subfolder="sdxl_models",
    weight_name=["ip-adapter_sdxl_vit-h.safetensors", "ip-adapter_sdxl_vit-h.safetensors"]
)

# ---------------------------------------------------------------------------
# 2. Request / Response Contracts
# ---------------------------------------------------------------------------
class GenerateRequest(BaseModel):
    prompt: str
    negative_prompt: Optional[str] = (
        "lowres, bad anatomy, bad hands, text, error, missing fingers, "
        "extra digit, fewer digits, cropped, worst quality, low quality, "
        "normal quality, jpeg artifacts, signature, watermark, username, blurry, artist name"
    )
    width: int = 1024
    height: int = 1024
    num_inference_steps: int = 28
    guidance_scale: float = 7.0
    reference_image: Optional[str] = None          # Slot 1: Master Identity Anchor
    previous_scene_image: Optional[str] = None      # Slot 2: Scene Context Frame N-1
    identity_scale: float = 0.65                    # Configurable Identity Weight
    scene_scale: float = 0.20                       # Configurable Scene Weight
    seed: Optional[int] = None

class GenerateResponse(BaseModel):
    image: str # Base64 Data URL or Public URL
    seed_used: int
    identity_scale_used: float
    scene_scale_used: float

def fetch_image(image_source: str) -> Image.Image:
    """Fetch image from HTTP URL or decode from Base64 string."""
    try:
        if image_source.startswith("http://") or image_source.startswith("https://"):
            response = requests.get(image_source, timeout=15)
            response.raise_for_status()
            return Image.open(BytesIO(response.content)).convert("RGB")
        elif image_source.startswith("data:image"):
            base64_data = image_source.split(",")[1]
            return Image.open(BytesIO(base64.b64decode(base64_data))).convert("RGB")
        else:
            return Image.open(BytesIO(base64.b64decode(image_source))).convert("RGB")
    except Exception as e:
        raise HTTPException(status_code=400, detail=f"Failed to load conditioning image: {str(e)}")

# ---------------------------------------------------------------------------
# 3. Conditioning Inference Endpoint
# ---------------------------------------------------------------------------
@app.post("/generate", response_model=GenerateResponse)
async def generate(req: GenerateRequest):
    try:
        # Prepare seeds
        generator = None
        seed_used = req.seed if req.seed is not None else torch.randint(0, 2**32 - 1, (1,)).item()
        generator = torch.Generator(device="cuda").manual_seed(seed_used)

        # Prepare conditioning inputs for Dual Slots
        ip_adapter_images: List[Image.Image] = []
        ip_adapter_scales: List[float] = []

        # Slot 1: Master Identity (Always loaded if present)
        if req.reference_image:
            identity_img = fetch_image(req.reference_image)
            ip_adapter_images.append(identity_img)
            ip_adapter_scales.append(req.identity_scale)
        else:
            # Blank 1x1 fallback to keep slot alignment
            ip_adapter_images.append(Image.new("RGB", (224, 224), (0, 0, 0)))
            ip_adapter_scales.append(0.0)

        # Slot 2: Scene Background Context (Loaded if present)
        if req.previous_scene_image:
            scene_img = fetch_image(req.previous_scene_image)
            ip_adapter_images.append(scene_img)
            ip_adapter_scales.append(req.scene_scale)
        else:
            # Blank fallback for Slot 2
            ip_adapter_images.append(Image.new("RGB", (224, 224), (0, 0, 0)))
            ip_adapter_scales.append(0.0)

        # Set multi-adapter scales
        pipe.set_ip_adapter_scale(ip_adapter_scales)

        # Execute diffusion generation
        output = pipe(
            prompt=req.prompt,
            negative_prompt=req.negative_prompt,
            ip_adapter_image=ip_adapter_images,
            width=req.width,
            height=req.height,
            num_inference_steps=req.num_inference_steps,
            guidance_scale=req.guidance_scale,
            generator=generator
        )

        result_image = output.images[0]

        # Convert to Base64 data URL
        buffered = BytesIO()
        result_image.save(buffered, format="PNG")
        img_str = base64.b64encode(buffered.getvalue()).decode("utf-8")
        data_url = f"data:image/png;base64,{img_str}"

        return GenerateResponse(
            image=data_url,
            seed_used=seed_used,
            identity_scale_used=req.identity_scale,
            scene_scale_used=req.scene_scale
        )

    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Generation failed: {str(e)}")

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000)
