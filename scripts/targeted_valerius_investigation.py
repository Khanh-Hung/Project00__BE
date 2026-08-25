import os
import sys
import json
import time
import shutil
import urllib.request
import torch
import numpy as np

try:
    sys.stdout.reconfigure(line_buffering=True)
except Exception:
    pass
from PIL import Image, ImageDraw, ImageFont
from transformers import CLIPVisionModelWithProjection, CLIPImageProcessor, CLIPTokenizer, CLIPTextModelWithProjection

COMFY_URL = "http://127.0.0.1:8188"
COMFY_INPUT_DIR = r"D:\ComfyUI_windows_portable\ComfyUI\input"
COMFY_OUTPUT_DIR = r"D:\ComfyUI_windows_portable\ComfyUI\output"
EVAL_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "eval_artifacts_v23", "valerius_ablation"))
os.makedirs(EVAL_DIR, exist_ok=True)

# Load CLIP Evaluator
clip_model_id = "openai/clip-vit-large-patch14"
device = "cuda" if torch.cuda.is_available() else "cpu"
processor = CLIPImageProcessor.from_pretrained(clip_model_id)
vision_model = CLIPVisionModelWithProjection.from_pretrained(clip_model_id).to(device).eval()
tokenizer = CLIPTokenizer.from_pretrained(clip_model_id)
text_model = CLIPTextModelWithProjection.from_pretrained(clip_model_id).to(device).eval()

def get_full_image_embedding(image_path):
    image = Image.open(image_path).convert("RGB")
    inputs = processor(images=image, return_tensors="pt").to(device)
    with torch.no_grad():
        outputs = vision_model(**inputs)
        emb = outputs.image_embeds
        emb = emb / emb.norm(p=2, dim=-1, keepdim=True)
    return emb.cpu().numpy()[0]

def get_text_embedding(text):
    inputs = tokenizer([text], padding=True, return_tensors="pt").to(device)
    with torch.no_grad():
        outputs = text_model(**inputs)
        emb = outputs.text_embeds
        emb = emb / emb.norm(p=2, dim=-1, keepdim=True)
    return emb.cpu().numpy()[0]

def compute_similarity(emb1, emb2):
    return float(np.dot(emb1, emb2) / (np.linalg.norm(emb1) * np.linalg.norm(emb2)))

def evaluate_gender(image_path):
    img_emb = get_full_image_embedding(image_path)
    pos_emb = get_text_embedding("1man, anime man, male, boy, masculine face, handsome male knight")
    neg_emb = get_text_embedding("1girl, anime girl, female, woman, breasts, feminine face")
    pos_sim = compute_similarity(img_emb, pos_emb)
    neg_sim = compute_similarity(img_emb, neg_emb)
    return bool(pos_sim > neg_sim), pos_sim, neg_sim

def queue_prompt(prompt_workflow):
    data = json.dumps({"prompt": prompt_workflow}).encode('utf-8')
    req = urllib.request.Request(f"{COMFY_URL}/prompt", data=data, headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(req) as resp:
        return json.loads(resp.read())

def wait_for_prompt_completion(prompt_id, timeout=120):
    start = time.time()
    while time.time() - start < timeout:
        req = urllib.request.Request(f"{COMFY_URL}/history/{prompt_id}")
        with urllib.request.urlopen(req) as resp:
            history = json.loads(resp.read())
            if prompt_id in history:
                outputs = history[prompt_id].get("outputs", {})
                for node_id, node_out in outputs.items():
                    if "images" in node_out:
                        img_info = node_out["images"][0]
                        filename = img_info["filename"]
                        subfolder = img_info.get("subfolder", "")
                        return os.path.join(COMFY_OUTPUT_DIR, subfolder, filename)
        time.sleep(1.0)
    raise TimeoutError(f"Prompt {prompt_id} did not finish within {timeout}s.")

def load_v2_workflow_template():
    workflow_path = os.path.abspath(os.path.join(os.path.dirname(__file__), "production_workflow_v2_template.json"))
    with open(workflow_path, "r", encoding="utf-8") as f:
        return json.load(f)

def build_workflow(template, avatar_filename, prev_scene_filename, prompt_text, negative_text, seed, weight=0.15, end_at=0.30, slot2_active=True):
    wf = json.loads(json.dumps(template))
    wf["6"]["inputs"]["text"] = prompt_text
    wf["7"]["inputs"]["text"] = negative_text
    wf["3"]["inputs"]["seed"] = seed
    wf["1"]["inputs"]["image"] = avatar_filename

    if slot2_active and prev_scene_filename:
        wf["13"]["inputs"]["image"] = prev_scene_filename
        wf["14"]["inputs"]["weight"] = float(weight)
        wf["14"]["inputs"]["end_at"] = float(end_at)
        wf["3"]["inputs"]["model"] = ["14", 0]
    else:
        wf["3"]["inputs"]["model"] = ["10", 0]

    return wf

template = load_v2_workflow_template()

# Read the C# compiled requests for Valerius
with open(r"G:\New folder (5)\BE\eval_artifacts_v23\authoritative_compiled_requests.json", "r", encoding="utf-8") as f:
    all_reqs = json.load(f)
valerius_reqs = [r for r in all_reqs if r["CharacterId"] == "character_03_valerius"]

print("=" * 100)
print("VALERIUS TARGETED ABLATION: ISOLATING CAUSE OF GENDER FLIP ACROSS 4 CONFIGURATIONS")
print("=" * 100)

configs = [
    {
        "name": "Config A: Baseline (V2 Production: Slot2 0.15/0.30 same-scene, 0.08/0.20 transition)",
        "slot2_mode": "production",
        "male_reinforce": False
    },
    {
        "name": "Config B: Slot 2 Completely Disabled (Slot 1 Canonical Avatar Only)",
        "slot2_mode": "disabled",
        "male_reinforce": False
    },
    {
        "name": "Config C: Attenuated Slot 2 (Weight 0.04, EndAt 0.15 across all turns)",
        "slot2_mode": "attenuated",
        "male_reinforce": False
    },
    {
        "name": "Config D: Stronger Anatomical Negative (Anti-Breast + Flat Chest Anchor)",
        "slot2_mode": "production",
        "male_reinforce": True
    }
]

summary_ablation = []

for idx, cfg in enumerate(configs):
    print(f"\n--- Testing {cfg['name']} ---")
    cfg_dir = os.path.join(EVAL_DIR, f"config_{chr(65 + idx)}")
    os.makedirs(cfg_dir, exist_ok=True)
    
    prev_scene = None
    gender_passes = 0
    turn_results = []
    
    for req in valerius_reqs:
        turn = req["Turn"]
        seed = req["Seed"]
        prompt = req["CompiledPrompt"]
        negative = req["CompiledNegative"]
        is_trans = req["IsTransition"]
        
        if cfg["male_reinforce"]:
            prompt = prompt.replace("1man, male, masculine face", "1man, male, handsome male knight, defined masculine jawline, flat male chest")
            negative = negative + ", breasts, cleavage, feminine curves, female body shape, girl, woman"
            
        if cfg["slot2_mode"] == "disabled":
            weight, end_at, slot2_active = 0.0, 0.0, False
        elif cfg["slot2_mode"] == "attenuated":
            weight, end_at, slot2_active = (0.0, 0.0, False) if turn == 1 else (0.04, 0.15, True)
        else: # production
            weight = req["Slot2Weight"]
            end_at = req["Slot2EndAt"]
            slot2_active = req["Slot2Active"]
            
        wf = build_workflow(template, "Valerius_tight_face.png", prev_scene, prompt, negative, seed, weight=weight, end_at=end_at, slot2_active=slot2_active)
        q = queue_prompt(wf)
        gen_path = wait_for_prompt_completion(q["prompt_id"])
        
        artifact_path = os.path.join(cfg_dir, f"turn_{turn:02d}.png")
        shutil.copyfile(gen_path, artifact_path)
        
        next_input = f"valerius_abl_{turn}_input.png"
        shutil.copyfile(gen_path, os.path.join(COMFY_INPUT_DIR, next_input))
        prev_scene = next_input
        
        is_male, pos_s, neg_s = evaluate_gender(gen_path)
        if is_male:
            gender_passes += 1
            
        print(f"  Turn {turn:02d} | Seed: {seed} | Gender: {'MALE (✓)' if is_male else 'FEMALE (✗)'} (Pos: {pos_s:.4f}, Neg: {neg_s:.4f})")
        turn_results.append({"turn": turn, "is_male": is_male, "pos_s": pos_s, "neg_s": neg_s})
        
    print(f"Result for {cfg['name']}: {gender_passes}/8 Male Retention")
    summary_ablation.append({
        "config": cfg["name"],
        "gender_retention": f"{gender_passes}/8 ({gender_passes/8*100:.1f}%)",
        "turns": turn_results
    })

print("\n" + "=" * 100)
print("TARGETED ABLATION SUMMARY TABLE")
print("=" * 100)
for s in summary_ablation:
    print(f"{s['config']:<60} | Retention: {s['gender_retention']}")
print("=" * 100)

with open(os.path.join(EVAL_DIR, "targeted_ablation_summary.json"), "w", encoding="utf-8") as f:
    json.dump(summary_ablation, f, indent=2)
