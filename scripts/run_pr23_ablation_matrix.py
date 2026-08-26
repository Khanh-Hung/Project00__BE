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
EVAL_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "eval_artifacts_pr23"))
os.makedirs(EVAL_DIR, exist_ok=True)

# 1. Load CLIP Evaluator
clip_model_id = "openai/clip-vit-large-patch14"
device = "cuda" if torch.cuda.is_available() else "cpu"
processor = CLIPImageProcessor.from_pretrained(clip_model_id)
vision_model = CLIPVisionModelWithProjection.from_pretrained(clip_model_id).to(device).eval()
tokenizer = CLIPTokenizer.from_pretrained(clip_model_id)
text_model = CLIPTextModelWithProjection.from_pretrained(clip_model_id).to(device).eval()

def get_image_embedding(image_path):
    img = Image.open(image_path).convert("RGB")
    inputs = processor(images=img, return_tensors="pt").to(device)
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

def build_workflow(template, avatar_filename, prev_scene_filename, prompt_text, negative_text, seed, weight=0.15, end_at=0.30, weight_type="linear", slot2_active=True):
    wf = json.loads(json.dumps(template))
    wf["6"]["inputs"]["text"] = prompt_text
    wf["7"]["inputs"]["text"] = negative_text
    wf["3"]["inputs"]["seed"] = seed
    wf["1"]["inputs"]["image"] = avatar_filename

    if slot2_active and prev_scene_filename:
        wf["13"]["inputs"]["image"] = prev_scene_filename
        wf["14"]["inputs"]["weight"] = float(weight)
        wf["14"]["inputs"]["end_at"] = float(end_at)
        wf["14"]["inputs"]["weight_type"] = weight_type
        wf["3"]["inputs"]["model"] = ["14", 0]
    else:
        wf["3"]["inputs"]["model"] = ["10", 0]

    return wf

def create_contact_sheet(image_paths, titles, output_path, avatar_path=None):
    images = []
    if avatar_path and os.path.exists(avatar_path):
        images.append((Image.open(avatar_path).convert("RGB"), "CANONICAL AVATAR"))
    for p, t in zip(image_paths, titles):
        if os.path.exists(p):
            images.append((Image.open(p).convert("RGB"), t))
            
    if not images:
        return

    cols = 3
    rows = (len(images) + cols - 1) // cols
    cell_w, cell_h = 512, 768
    pad = 40
    sheet = Image.new("RGB", (cols * cell_w, rows * (cell_h + pad)), color=(20, 20, 25))
    draw = ImageDraw.Draw(sheet)

    try:
        font = ImageFont.truetype("arial.ttf", 22)
    except Exception:
        font = ImageFont.load_default()

    for idx, (img, title) in enumerate(images):
        c = idx % cols
        r = idx // cols
        x = c * cell_w
        y = r * (cell_h + pad)
        resized = img.resize((cell_w, cell_h), Image.Resampling.LANCZOS)
        sheet.paste(resized, (x, y + pad))
        draw.text((x + 10, y + 8), title, fill=(255, 215, 0) if "AVATAR" in title else (255, 255, 255), font=font)

    sheet.save(output_path)

# Load Authoritative C# Requests
with open(r"G:\New folder (5)\BE\eval_artifacts_v23\authoritative_compiled_requests.json", "r", encoding="utf-8") as f:
    all_requests = json.load(f)

# Focus persona scenarios: Valerius (P0 subject), Elysia (control), Lyra (control)
personas = ["character_03_valerius", "character_02_elysia", "character_01_lyra"]

# Define 6 Ablation Configurations
configs = [
    {
        "id": "A",
        "name": "Config A: Baseline V2 (Linear Slot2: 0.15/0.30 same, 0.08/0.20 trans)",
        "slot2_mode": "linear_production",
        "weight_type": "linear",
        "anatomical_gate": False
    },
    {
        "id": "B",
        "name": "Config B: Slot 2 Disabled (Cold Start / Slot 1 Only)",
        "slot2_mode": "disabled",
        "weight_type": "linear",
        "anatomical_gate": False
    },
    {
        "id": "C",
        "name": "Config C: Deep Attenuation (Linear 0.04/0.15)",
        "slot2_mode": "attenuated",
        "weight_type": "linear",
        "anatomical_gate": False
    },
    {
        "id": "D",
        "name": "Config D: Layer-Gated Style Transfer (Slot2 weight_type='style transfer', Down/Mid blocks only)",
        "slot2_mode": "layer_gated",
        "weight_type": "style transfer",
        "anatomical_gate": False
    },
    {
        "id": "E",
        "name": "Config E: Anatomical Invariant Anchors (Linear Slot2 + Anti-Breasts/Flat Chest)",
        "slot2_mode": "linear_production",
        "weight_type": "linear",
        "anatomical_gate": True
    },
    {
        "id": "F",
        "name": "Config F: Candidate (Layer-Gated 'style transfer' 0.10/0.25 + Anatomical Gate)",
        "slot2_mode": "candidate",
        "weight_type": "style transfer",
        "anatomical_gate": True
    }
]

template = load_v2_workflow_template()

# Feature & Attribute Embeddings for Evaluation
feat_prompts = {
    "Male_pos": "1man, anime man, male, boy, masculine face, handsome male knight",
    "Male_neg": "1girl, anime girl, female, woman, breasts, feminine face",
    "Female_pos": "1girl, anime girl, female, woman, feminine face",
    "Female_neg": "1man, anime man, male, boy, masculine face",
    "character_01_lyra": {
        "feat_pos": "sharp black dragon horns with glowing red accents on head",
        "feat_neg": "smooth human head without horns, normal human ears",
        "avatar": os.path.join(COMFY_INPUT_DIR, "Lyra_tight_face.png")
    },
    "character_02_elysia": {
        "feat_pos": "long elegant pointed elf ears",
        "feat_neg": "round human ears, short ears",
        "avatar": os.path.join(COMFY_INPUT_DIR, "Elysia_tight_face.png")
    },
    "character_03_valerius": {
        "feat_pos": "dark steel knight commander armor with silver trims",
        "feat_neg": "casual t-shirt, modern suit, swimsuit, dress",
        "avatar": os.path.join(COMFY_INPUT_DIR, "Valerius_tight_face.png")
    }
}

matrix_results = []

print("=" * 110)
print("PR #23 SYSTEMATIC MULTI-DIMENSIONAL ABLATION MATRIX EXECUTION (RTX 3050 Ti)")
print("=" * 110)

for cfg in configs:
    cfg_id = cfg["id"]
    cfg_name = cfg["name"]
    print(f"\n==========================================================================================")
    print(f"RUNNING {cfg_name}")
    print(f"==========================================================================================")
    
    cfg_dir = os.path.join(EVAL_DIR, f"config_{cfg_id}")
    os.makedirs(cfg_dir, exist_ok=True)
    
    cfg_summary = {
        "config_id": cfg_id,
        "config_name": cfg_name,
        "characters": {}
    }
    
    for char_id in personas:
        char_reqs = [r for r in all_requests if r["CharacterId"] == char_id]
        if not char_reqs:
            continue
            
        char_name = char_reqs[0]["CharacterName"]
        char_gender = char_reqs[0]["Gender"]
        avatar_path = feat_prompts[char_id]["avatar"]
        avatar_emb = get_image_embedding(avatar_path) if os.path.exists(avatar_path) else None
        
        char_dir = os.path.join(cfg_dir, char_id)
        os.makedirs(char_dir, exist_ok=True)
        
        prev_scene_filename = None
        prev_scene_emb = None
        
        gender_passes = 0
        feat_passes = 0
        face_identities = []
        action_margins = []
        same_scene_sims = []
        trans_scene_sims = []
        
        frame_paths = []
        frame_titles = []
        
        for req in char_reqs:
            turn = req["Turn"]
            seed = req["Seed"]
            prompt = req["CompiledPrompt"]
            negative = req["CompiledNegative"]
            is_trans = req["IsTransition"]
            location = req["Location"]
            
            # Apply Anatomical Gating if enabled for this config
            if cfg["anatomical_gate"] and char_gender == "Male":
                prompt = prompt.replace("1man, male, masculine face", "1man, male, masculine face, handsome male knight, defined masculine jawline, flat male chest")
                negative = negative + ", breasts, cleavage, feminine curves, female body shape, girl, woman"
                
            # Determine Slot 2 parameters based on config mode
            if cfg["slot2_mode"] == "disabled":
                weight, end_at, slot2_active = 0.0, 0.0, False
            elif cfg["slot2_mode"] == "attenuated":
                weight, end_at, slot2_active = (0.0, 0.0, False) if turn == 1 else (0.04, 0.15, True)
            elif cfg["slot2_mode"] == "layer_gated":
                weight = 0.15 if not is_trans else 0.08
                end_at = 0.30 if not is_trans else 0.20
                slot2_active = turn > 1
            elif cfg["slot2_mode"] == "candidate":
                weight = 0.12 if not is_trans else 0.06
                end_at = 0.25 if not is_trans else 0.15
                slot2_active = turn > 1
            else: # linear_production
                weight = req["Slot2Weight"]
                end_at = req["Slot2EndAt"]
                slot2_active = req["Slot2Active"]
                
            wf = build_workflow(
                template=template,
                avatar_filename=os.path.basename(avatar_path),
                prev_scene_filename=prev_scene_filename,
                prompt_text=prompt,
                negative_text=negative,
                seed=seed,
                weight=weight,
                end_at=end_at,
                weight_type=cfg["weight_type"],
                slot2_active=slot2_active
            )
            
            frame_artifact = os.path.join(char_dir, f"turn_{turn:02d}.png")
            next_input = f"{char_id}_pr23_cfg{cfg_id}_t{turn}.png"
            comfy_input_dest = os.path.join(COMFY_INPUT_DIR, next_input)
            
            if os.path.exists(frame_artifact):
                gen_path = frame_artifact
                if not os.path.exists(comfy_input_dest):
                    shutil.copyfile(gen_path, comfy_input_dest)
            else:
                q = queue_prompt(wf)
                gen_path = wait_for_prompt_completion(q["prompt_id"])
                shutil.copyfile(gen_path, frame_artifact)
                shutil.copyfile(gen_path, comfy_input_dest)
                
            frame_paths.append(frame_artifact)
            frame_titles.append(f"Turn {turn:02d} ({location[:16]})")
            prev_scene_filename = next_input
            
            # 1. Image Embedding & Face Identity
            img_emb = get_image_embedding(gen_path)
            face_sim = compute_similarity(img_emb, avatar_emb) if avatar_emb is not None else 0.0
            face_identities.append(face_sim)
            
            # 2. Gender Presentation (CLIP heuristic)
            if char_gender == "Male":
                g_pos = compute_similarity(img_emb, get_text_embedding(feat_prompts["Male_pos"]))
                g_neg = compute_similarity(img_emb, get_text_embedding(feat_prompts["Male_neg"]))
            else:
                g_pos = compute_similarity(img_emb, get_text_embedding(feat_prompts["Female_pos"]))
                g_neg = compute_similarity(img_emb, get_text_embedding(feat_prompts["Female_neg"]))
            gender_pass = bool(g_pos > g_neg)
            if gender_pass:
                gender_passes += 1
                
            # 3. Signature Feature Retention
            f_pos = compute_similarity(img_emb, get_text_embedding(feat_prompts[char_id]["feat_pos"]))
            f_neg = compute_similarity(img_emb, get_text_embedding(feat_prompts[char_id]["feat_neg"]))
            feat_pass = bool(f_pos > f_neg)
            if feat_pass:
                feat_passes += 1
                
            # 4. Action Margin
            target_act_emb = get_text_embedding(req["TargetActionPrompt"])
            distractor_act_embs = [get_text_embedding(d) for d in req["NegativeActionPrompts"]]
            target_sim = compute_similarity(img_emb, target_act_emb)
            max_dist_sim = max(compute_similarity(img_emb, d_emb) for d_emb in distractor_act_embs)
            action_margin = float(target_sim - max_dist_sim)
            action_margins.append(action_margin)
            
            # 5. Scene Continuity
            if prev_scene_emb is not None:
                scene_sim = compute_similarity(img_emb, prev_scene_emb)
                if is_trans:
                    trans_scene_sims.append(scene_sim)
                else:
                    same_scene_sims.append(scene_sim)
            prev_scene_emb = img_emb
            
            print(f"[{char_name} | T{turn}] FaceId: {face_sim:.4f} | Gender: {'MALE (✓)' if gender_pass else 'FEMALE (✗)' if char_gender == 'Male' else 'FEMALE (✓)'} | Feat: {'✓' if feat_pass else '✗'} | Margin: {action_margin:+.4f}")
            
        contact_path = os.path.join(char_dir, f"{char_id}_contact_sheet.png")
        create_contact_sheet(frame_paths, frame_titles, contact_path, avatar_path)
        
        mean_face_id = float(np.mean(face_identities))
        mean_action_margin = float(np.mean(action_margins))
        mean_same_scene = float(np.mean(same_scene_sims)) if same_scene_sims else 1.0
        mean_trans_scene = float(np.mean(trans_scene_sims)) if trans_scene_sims else 0.0
        
        cfg_summary["characters"][char_id] = {
            "name": char_name,
            "gender_retention": f"{gender_passes}/8 ({gender_passes/8*100:.1f}%)",
            "gender_passes": gender_passes,
            "feature_retention": f"{feat_passes}/8 ({feat_passes/8*100:.1f}%)",
            "feature_passes": feat_passes,
            "mean_face_identity": round(mean_face_id, 4),
            "mean_action_margin": round(mean_action_margin, 4),
            "same_scene_continuity": round(mean_same_scene, 4),
            "transition_continuity": round(mean_trans_scene, 4)
        }
        
    matrix_results.append(cfg_summary)

with open(os.path.join(EVAL_DIR, "ablation_comparison_matrix.json"), "w", encoding="utf-8") as f:
    json.dump(matrix_results, f, indent=2)

print("\n" + "=" * 120)
print("PR #23 SYSTEMATIC ABLATION MATRIX SUMMARY")
print("=" * 120)
print(f"{'Config ID & Name':<50} | {'Valerius Gender':<16} | {'Valerius FaceId':<16} | {'Lyra Feat':<12} | {'Elysia Ears':<12} | {'Same Scene':<12}")
print("-" * 120)
for r in matrix_results:
    cid = r["config_id"]
    cname = r["config_name"][:46]
    val_g = r["characters"].get("character_03_valerius", {}).get("gender_retention", "N/A")
    val_f = r["characters"].get("character_03_valerius", {}).get("mean_face_identity", "N/A")
    lyra_f = r["characters"].get("character_01_lyra", {}).get("feature_retention", "N/A")
    ely_f = r["characters"].get("character_02_elysia", {}).get("feature_retention", "N/A")
    same_s = r["characters"].get("character_03_valerius", {}).get("same_scene_continuity", "N/A")
    print(f"{cname:<50} | {val_g:<16} | {val_f:<16} | {lyra_f:<12} | {ely_f:<12} | {same_s:<12}")
print("=" * 120)
