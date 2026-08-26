"""
PR #24 Stage 1 & Stage 2 Identity Quality Guard Benchmark Runner
Evaluates 2 modes:
  Mode 1: Baseline PR23 (Unguarded, 1-shot)
  Mode 2: PR24 Identity Quality Guard (Quality Evaluation + Deterministic Mitigation Loop + Quarantine)

Stage 1: 3 personas x 4 turns x 2 modes = 24 GPU frames
Stage 2: 3 personas x 8 turns x 2 modes = 48 GPU frames
"""

import os
import sys

try:
    sys.stdout.reconfigure(line_buffering=True)
except Exception:
    pass

import json
import time
import shutil
import urllib.request
import urllib.parse
import numpy as np
from PIL import Image, ImageDraw, ImageFont
import torch
from transformers import CLIPImageProcessor, CLIPVisionModelWithProjection, CLIPTokenizer, CLIPTextModelWithProjection

COMFY_HOST = "127.0.0.1:8188"
COMFY_OUTPUT_DIR = r"D:\ComfyUI_windows_portable\ComfyUI\output"
INPUT_DIR = r"D:\ComfyUI_windows_portable\ComfyUI\input"
BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REQUESTS_FILE = os.path.join(BASE_DIR, "eval_artifacts_v23", "authoritative_compiled_requests.json")
TEMPLATE_PATH = os.path.join(BASE_DIR, "scripts", "production_workflow_v2_template.json")
ARTIFACTS_DIR = os.path.join(BASE_DIR, "eval_artifacts_pr24")
os.makedirs(ARTIFACTS_DIR, exist_ok=True)

# -------------------------------------------------------------
# CLIP Evaluator Initialization
# -------------------------------------------------------------
clip_model_id = "openai/clip-vit-large-patch14"
device = "cuda" if torch.cuda.is_available() else "cpu"
print(f"Loading CLIP Evaluator ({clip_model_id}) on {device}...", flush=True)
clip_processor = CLIPImageProcessor.from_pretrained(clip_model_id)
clip_vision_model = CLIPVisionModelWithProjection.from_pretrained(clip_model_id).to(device).eval()
clip_tokenizer = CLIPTokenizer.from_pretrained(clip_model_id)
clip_text_model = CLIPTextModelWithProjection.from_pretrained(clip_model_id).to(device).eval()
print(f"CLIP Evaluator ready on {device}!", flush=True)

def get_image_embedding(image_path: str):
    img = Image.open(image_path).convert("RGB")
    inputs = clip_processor(images=img, return_tensors="pt").to(device)
    with torch.no_grad():
        outputs = clip_vision_model(**inputs)
        emb = outputs.image_embeds[0].cpu().numpy()
        emb = emb / (np.linalg.norm(emb) + 1e-8)
    return emb

def get_text_embedding(text: str):
    inputs = clip_tokenizer(text=[text], return_tensors="pt", padding=True, truncation=True).to(device)
    with torch.no_grad():
        outputs = clip_text_model(**inputs)
        emb = outputs.text_embeds[0].cpu().numpy()
        emb = emb / (np.linalg.norm(emb) + 1e-8)
    return emb

def cosine_similarity(a: np.ndarray, b: np.ndarray) -> float:
    return float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b) + 1e-8))

# -------------------------------------------------------------
# Deterministic Seed Derivation (Matches C# SplitMix64)
# -------------------------------------------------------------
def derive_seed(base_seed: int, attempt: int) -> int:
    if attempt <= 1:
        return int(base_seed)
    z = (base_seed + (attempt * 0x9E3779B97F4A7C15)) & 0xFFFFFFFFFFFFFFFF
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9 & 0xFFFFFFFFFFFFFFFF
    z = (z ^ (z >> 27)) * 0x94D049BB133111EB & 0xFFFFFFFFFFFFFFFF
    res = (z ^ (z >> 31)) & 0x7FFFFFFFFFFFFFFF
    return int(res)

# -------------------------------------------------------------
# ComfyUI Dispatcher
# -------------------------------------------------------------
def queue_prompt(prompt_workflow):
    data = json.dumps({"prompt": prompt_workflow}).encode("utf-8")
    req = urllib.request.Request(f"http://{COMFY_HOST}/prompt", data=data, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req) as resp:
        return json.loads(resp.read().decode("utf-8"))

def wait_for_execution(prompt_id, timeout=120):
    start = time.time()
    while time.time() - start < timeout:
        try:
            with urllib.request.urlopen(f"http://{COMFY_HOST}/history/{prompt_id}") as resp:
                history = json.loads(resp.read().decode("utf-8"))
                if prompt_id in history:
                    outputs = history[prompt_id].get("outputs", {})
                    for nid, nout in outputs.items():
                        if "images" in nout and len(nout["images"]) > 0:
                            img_info = nout["images"][0]
                            fn = img_info["filename"]
                            sub = img_info.get("subfolder", "")
                            if sub:
                                return os.path.join(sub, fn)
                            return fn
        except Exception:
            pass
        time.sleep(1.0)
    raise TimeoutError(f"Generation timed out after {timeout}s for prompt_id {prompt_id}")

def build_workflow_graph(template, prompt_text, negative_text, seed, avatar_file, prev_scene_file=None, slot1_weight=0.60, slot1_end_at=0.85, slot2_weight=0.12, slot2_end_at=0.25, weight_type="style transfer"):
    import copy
    wf = copy.deepcopy(template)
    wf["6"]["inputs"]["text"] = prompt_text
    wf["7"]["inputs"]["text"] = negative_text
    wf["3"]["inputs"]["seed"] = int(seed)
    wf["1"]["inputs"]["image"] = os.path.basename(avatar_file)

    if "10" in wf:
        wf["10"]["inputs"]["weight"] = round(float(slot1_weight), 4)
        wf["10"]["inputs"]["end_at"] = round(float(slot1_end_at), 4)

    if prev_scene_file and os.path.exists(prev_scene_file) and slot2_weight > 0.0:
        wf["13"]["inputs"]["image"] = os.path.basename(prev_scene_file)
        wf["14"]["inputs"]["weight"] = round(float(slot2_weight), 4)
        wf["14"]["inputs"]["end_at"] = round(float(slot2_end_at), 4)
        wf["14"]["inputs"]["weight_type"] = weight_type
        wf["3"]["inputs"]["model"] = ["14", 0]
    else:
        # Full bypass: remove Node 13 & 14, connect KSampler directly to Node 10
        wf.pop("13", None)
        wf.pop("14", None)
        wf["3"]["inputs"]["model"] = ["10", 0]

    return wf

# -------------------------------------------------------------
# Contact Sheet Generator
# -------------------------------------------------------------
def create_contact_sheet(frame_paths, frame_labels, out_path, avatar_path):
    cols = len(frame_paths) + 1
    w, h = 300, 450
    margin = 10
    sheet_w = cols * w + (cols + 1) * margin
    sheet_h = h + 2 * margin + 40
    sheet = Image.new("RGB", (sheet_w, sheet_h), (25, 25, 30))
    draw = ImageDraw.Draw(sheet)

    # Place avatar
    if os.path.exists(avatar_path):
        av_img = Image.open(avatar_path).convert("RGB").resize((w, h))
        sheet.paste(av_img, (margin, margin))
        draw.text((margin + 10, h + margin + 10), "Canonical Avatar", fill=(255, 215, 0))

    # Place frames
    for i, (fp, lbl) in enumerate(zip(frame_paths, frame_labels)):
        if os.path.exists(fp):
            f_img = Image.open(fp).convert("RGB").resize((w, h))
            x = (i + 1) * w + (i + 2) * margin
            sheet.paste(f_img, (x, margin))
            draw.text((x + 10, h + margin + 10), lbl, fill=(240, 240, 240))

    sheet.save(out_path)

# -------------------------------------------------------------
# Identity Quality Evaluation Contract
# -------------------------------------------------------------
def evaluate_frame_quality(img_path, avatar_path, char_name, turn_data):
    img_emb = get_image_embedding(img_path)
    avatar_emb = get_image_embedding(avatar_path)
    face_sim = float(cosine_similarity(img_emb, avatar_emb))

    inv_violated = False
    if "Valerius" in char_name:
        g_male = cosine_similarity(img_emb, get_text_embedding("1man, handsome male knight, masculine face"))
        g_female = cosine_similarity(img_emb, get_text_embedding("1girl, anime girl, female, woman, breasts"))
        if g_female > g_male + 0.05:
            inv_violated = True

    feat_pass = True
    feat_score = 1.0
    if "Valerius" in char_name:
        f_pos = cosine_similarity(img_emb, get_text_embedding("black obsidian knight armor, silver filigree pauldron crest"))
        f_neg = cosine_similarity(img_emb, get_text_embedding("cloth tunic, casual modern shirt, everyday clothes"))
        feat_pass = (f_pos > f_neg)
        feat_score = float(max(0.0, min(1.0, (f_pos - f_neg + 0.1) * 5.0)))
    elif "Elysia" in char_name:
        f_pos = cosine_similarity(img_emb, get_text_embedding("pointed elf ears, delicate crystalline elf ear tips"))
        f_neg = cosine_similarity(img_emb, get_text_embedding("human round ears, normal human ears"))
        feat_pass = (f_pos > f_neg)
        feat_score = float(max(0.0, min(1.0, (f_pos - f_neg + 0.1) * 5.0)))
    elif "Lyra" in char_name:
        f_pos = cosine_similarity(img_emb, get_text_embedding("obsidian dragon horns, glowing crimson horn markings"))
        f_neg = cosine_similarity(img_emb, get_text_embedding("human head without horns, normal human head"))
        feat_pass = (f_pos > f_neg)
        feat_score = float(max(0.0, min(1.0, (f_pos - f_neg + 0.1) * 5.0)))

    overall = 0.6 * face_sim + 0.4 * feat_score
    if inv_violated:
        status = "Failed"
    elif face_sim < 0.72 or feat_score < 0.50 or not feat_pass:
        status = "Degraded"
    else:
        status = "Passed"

    eval_target_passed = (face_sim >= 0.75 and feat_score >= 0.50 and feat_pass and not inv_violated)

    return {
        "status": status,
        "face_similarity": float(face_sim),
        "feature_score": float(feat_score),
        "feature_passed": bool(feat_pass),
        "invariant_violated": bool(inv_violated),
        "overall_score": float(overall),
        "eval_target_passed": bool(eval_target_passed)
    }

# -------------------------------------------------------------
# Main Benchmark Execution
# -------------------------------------------------------------
def run_benchmark(max_turns=4, mode_filter=None):
    authoritative_plan_file = os.path.join(ARTIFACTS_DIR, "authoritative_pr24_plan.json")
    if os.path.exists(authoritative_plan_file):
        print(f"📖 Loading C# Authoritative PR24 Plan from: {authoritative_plan_file}", flush=True)
        with open(authoritative_plan_file, "r", encoding="utf-8") as f:
            all_requests = json.load(f)
    else:
        print(f"⚠️ Authoritative PR24 plan not found at {authoritative_plan_file}, falling back to {REQUESTS_FILE}", flush=True)
        with open(REQUESTS_FILE, "r", encoding="utf-8") as f:
            all_requests = json.load(f)

    with open(TEMPLATE_PATH, "r", encoding="utf-8") as f:
        template = json.load(f)

    # Group flat requests list by CharacterId
    characters_data = {}
    for r in all_requests:
        cid = r["CharacterId"]
        if cid not in characters_data:
            characters_data[cid] = {
                "name": r["CharacterName"],
                "canonical_avatar_url": r["IdentityReferenceUrl"],
                "gender": r.get("Gender", "Female"),
                "turns": []
            }
        
        attempts_list = r.get("Attempts", [])
        if not attempts_list:
            base_s = int(r["Seed"])
            attempts_list = [
                {"AttemptNumber": 1, "Seed": base_s, "Slot1Weight": 0.60, "Slot1EndAt": 0.85, "Slot2Weight": 0.12, "Slot2EndAt": 0.25, "WeightType": "style transfer", "Fingerprint": ""},
                {"AttemptNumber": 2, "Seed": derive_seed(base_s, 2), "Slot1Weight": 0.65, "Slot1EndAt": 0.85, "Slot2Weight": 0.06, "Slot2EndAt": 0.15, "WeightType": "style transfer", "Fingerprint": ""},
                {"AttemptNumber": 3, "Seed": derive_seed(base_s, 3), "Slot1Weight": 0.70, "Slot1EndAt": 0.85, "Slot2Weight": 0.0, "Slot2EndAt": 0.0, "WeightType": "style transfer", "Fingerprint": ""}
            ]

        characters_data[cid]["turns"].append({
            "scene_revision": r["Turn"],
            "seed": int(r.get("Seed", attempts_list[0]["Seed"])),
            "compiled_prompt": r["CompiledPrompt"],
            "compiled_negative_prompt": r["CompiledNegative"],
            "context": "ColdStart" if r["Turn"] == 1 else ("SceneTransition" if r.get("IsTransition") else "SameScene"),
            "target_action": r.get("TargetActionPrompt", ""),
            "negative_actions": r.get("NegativeActionPrompts", []),
            "attempts": attempts_list
        })

    modes = [
        {"id": "mode_1_baseline", "name": "Mode 1: Baseline PR23 (Unguarded 1-shot)", "guard": False},
        {"id": "mode_2_quality_guard", "name": "Mode 2: PR24 Identity Quality Guard (Auto-Mitigation + Quarantine)", "guard": True}
    ]

    if mode_filter:
        modes = [m for m in modes if m["id"] == mode_filter]

    results = []

    print(f"\n==========================================================================", flush=True)
    print(f"🚀 RUNNING PR #24 BENCHMARK: Max Turns={max_turns}, Total Modes={len(modes)}", flush=True)
    print(f"==========================================================================", flush=True)

    for mode in modes:
        mode_id = mode["id"]
        mode_name = mode["name"]
        use_guard = mode["guard"]
        mode_dir = os.path.join(ARTIFACTS_DIR, mode_id)
        os.makedirs(mode_dir, exist_ok=True)

        mode_summary = {"mode_id": mode_id, "mode_name": mode_name, "characters": {}}

        for char_key, char_info in characters_data.items():
            char_name = char_info["name"]
            avatar_url = char_info["canonical_avatar_url"]
            avatar_local = os.path.join(INPUT_DIR, os.path.basename(avatar_url))
            turns = char_info["turns"][:max_turns]

            print(f"\n--- [{mode_name}] Character: {char_name} ({len(turns)} turns) ---", flush=True)

            char_dir = os.path.join(mode_dir, char_key)
            os.makedirs(char_dir, exist_ok=True)

            last_known_good_image = None
            prev_frame_emb = None
            turn_results = []
            frame_paths = []
            frame_labels = []
            action_margins = []
            same_scene_sims = []
            trans_scene_sims = []

            for turn in turns:
                rev = turn["scene_revision"]
                base_seed = turn["seed"]
                prompt = turn["compiled_prompt"]
                neg = turn["compiled_negative_prompt"]
                context = turn["context"]
                is_cold = (context == "ColdStart") or (rev == 1)
                is_trans = (context == "SceneTransition")

                print(f"  Turn {rev} ({context}): BaseSeed={base_seed}", flush=True)

                max_attempts = 3 if use_guard else 1
                final_img_path = None
                final_eval = None
                final_seed = base_seed
                attempts_to_run = turn["attempts"][:max_attempts]

                for att_plan in attempts_to_run:
                    attempt = att_plan["AttemptNumber"]
                    cur_seed = att_plan["Seed"]
                    slot1_w = att_plan["Slot1Weight"]
                    slot1_e = att_plan["Slot1EndAt"]
                    slot2_w = att_plan["Slot2Weight"] if not is_cold else 0.0
                    slot2_e = att_plan["Slot2EndAt"] if not is_cold else 0.0
                    weight_type = att_plan["WeightType"]
                    fp = att_plan.get("Fingerprint", "")

                    prev_img_input = last_known_good_image if (not is_cold and last_known_good_image) else None

                    dest_name = f"{char_key}_turn_{rev:02d}_att_{attempt}.png"
                    dest_path = os.path.join(char_dir, dest_name)

                    if os.path.exists(dest_path) and os.path.getsize(dest_path) < 1000:
                        try:
                            os.remove(dest_path)
                        except Exception:
                            pass

                    if not os.path.exists(dest_path):
                        wf = build_workflow_graph(
                            template=template,
                            prompt_text=prompt,
                            negative_text=neg,
                            seed=cur_seed,
                            avatar_file=avatar_local,
                            prev_scene_file=prev_img_input,
                            slot1_weight=slot1_w,
                            slot1_end_at=slot1_e,
                            slot2_weight=slot2_w,
                            slot2_end_at=slot2_e,
                            weight_type=weight_type
                        )
                        q_res = queue_prompt(wf)
                        pid = q_res["prompt_id"]
                        fn = wait_for_execution(pid)
                        src_path = os.path.join(COMFY_OUTPUT_DIR, fn)
                        for _ in range(20):
                            if os.path.exists(src_path) and os.path.getsize(src_path) > 1000:
                                break
                            time.sleep(0.5)
                        shutil.copyfile(src_path, dest_path)

                    eval_res = evaluate_frame_quality(dest_path, avatar_local, char_name, turn)
                    final_eval = eval_res
                    final_img_path = dest_path
                    final_seed = cur_seed

                    print(f"    Attempt {attempt}: Status={eval_res['status']}, FaceSim={eval_res['face_similarity']:.4f}, FeatScore={eval_res['feature_score']:.4f}, Action={att_plan['MitigationAction']}", flush=True)

                    if not use_guard or eval_res["status"] == "Passed":
                        break

                # Continuity & Action metrics
                img_emb = get_image_embedding(final_img_path)
                if turn.get("target_action") and turn.get("negative_actions"):
                    target_emb = get_text_embedding(turn["target_action"])
                    neg_embs = [get_text_embedding(na) for na in turn["negative_actions"]]
                    t_sim = cosine_similarity(img_emb, target_emb)
                    max_n_sim = max(cosine_similarity(img_emb, ne) for ne in neg_embs)
                    margin = float(t_sim - max_n_sim)
                    action_margins.append(margin)

                if prev_frame_emb is not None:
                    c_sim = float(cosine_similarity(img_emb, prev_frame_emb))
                    if is_trans:
                        trans_scene_sims.append(c_sim)
                    else:
                        same_scene_sims.append(c_sim)
                prev_frame_emb = img_emb

                # Quarantine & Last-Known-Good Logic
                is_passed = (final_eval["status"] == "Passed") or (not use_guard)
                if is_passed:
                    last_known_good_image = final_img_path
                    comfy_prev_name = f"{char_key}_pr24_current.png"
                    comfy_prev_path = os.path.join(INPUT_DIR, comfy_prev_name)
                    shutil.copyfile(final_img_path, comfy_prev_path)
                    last_known_good_image = comfy_prev_path
                else:
                    print(f"    ⚠️ [QUARANTINE] Turn {rev} failed all attempts! Quarantined from continuity lineage.", flush=True)

                frame_paths.append(final_img_path)
                frame_labels.append(f"T{rev} ({final_eval['status']}) - Sim:{final_eval['face_similarity']:.2f}")

                turn_results.append({
                    "scene_revision": int(rev),
                    "attempts_used": int(attempt if attempt <= max_attempts else max_attempts),
                    "final_seed": int(final_seed),
                    "evaluation": final_eval,
                    "is_current": bool(is_passed),
                    "image_file": os.path.basename(final_img_path)
                })

            # Create Contact Sheet
            contact_sheet_path = os.path.join(char_dir, f"{char_key}_{mode_id}_contact_sheet.png")
            create_contact_sheet(frame_paths, frame_labels, contact_sheet_path, avatar_local)

            mean_face = float(np.mean([t["evaluation"]["face_similarity"] for t in turn_results]))
            mean_feat = float(np.mean([t["evaluation"]["feature_score"] for t in turn_results]))
            pass_count = int(sum(1 for t in turn_results if t["evaluation"]["status"] == "Passed"))
            mean_margin = float(np.mean(action_margins)) if action_margins else 0.0
            mean_same = float(np.mean(same_scene_sims)) if same_scene_sims else 1.0
            mean_trans = float(np.mean(trans_scene_sims)) if trans_scene_sims else 0.0

            mode_summary["characters"][char_key] = {
                "name": char_name,
                "mean_face_similarity": round(mean_face, 4),
                "mean_feature_score": round(mean_feat, 4),
                "action_margin": round(mean_margin, 4),
                "same_scene_continuity": round(mean_same, 4),
                "transition_continuity": round(mean_trans, 4),
                "passed_frames": f"{pass_count}/{len(turns)}",
                "contact_sheet": os.path.basename(contact_sheet_path),
                "turns": turn_results
            }

        results.append(mode_summary)

    out_json = os.path.join(ARTIFACTS_DIR, f"stage_{1 if max_turns==4 else 2}_matrix.json")
    with open(out_json, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2)
        f.flush()
        os.fsync(f.fileno())

    print(f"\n==========================================================================", flush=True)
    print(f"✅ BENCHMARK COMPLETED! Summary saved to {out_json}", flush=True)
    print(f"==========================================================================", flush=True)

    # Print Comparative Matrix Table
    print("\n" + "="*80, flush=True)
    print("📊 PR23 BASELINE vs PR24 IDENTITY QUALITY GUARD COMPARATIVE MATRIX", flush=True)
    print("="*80, flush=True)
    print(f"{'Metric':<32} | {'PR23 Baseline (Unguarded)':<22} | {'PR24 Guarded':<18}", flush=True)
    print("-"*80, flush=True)

    # Compute aggregate metrics across characters
    m1 = results[0]
    m2 = results[1]
    all_turns_m1 = [t for c in m1["characters"].values() for t in c["turns"]]
    all_turns_m2 = [t for c in m2["characters"].values() for t in c["turns"]]

    faces_m1 = [t["evaluation"]["face_similarity"] for t in all_turns_m1]
    faces_m2 = [t["evaluation"]["face_similarity"] for t in all_turns_m2]

    worst_id_m1 = min(faces_m1)
    worst_id_m2 = min(faces_m2)

    p10_m1 = float(np.percentile(faces_m1, 10))
    p10_m2 = float(np.percentile(faces_m2, 10))

    median_m1 = float(np.median(faces_m1))
    median_m2 = float(np.median(faces_m2))

    avg_face_m1 = float(np.mean(faces_m1))
    avg_face_m2 = float(np.mean(faces_m2))

    feats_m1 = [t["evaluation"]["feature_score"] for t in all_turns_m1]
    feats_m2 = [t["evaluation"]["feature_score"] for t in all_turns_m2]
    avg_feat_m1 = float(np.mean(feats_m1))
    avg_feat_m2 = float(np.mean(feats_m2))

    att1_pass_m2 = sum(1 for t in all_turns_m2 if t["attempts_used"] == 1 and t["evaluation"]["status"] == "Passed")
    att2_rec_m2 = sum(1 for t in all_turns_m2 if t["attempts_used"] == 2 and t["evaluation"]["status"] == "Passed")
    att3_rec_m2 = sum(1 for t in all_turns_m2 if t["attempts_used"] == 3 and t["evaluation"]["status"] == "Passed")
    retry_triggered_m2 = sum(1 for t in all_turns_m2 if t["attempts_used"] > 1)
    exhausted_m2 = sum(1 for t in all_turns_m2 if not t["is_current"])

    avg_attempts_m1 = 1.0
    avg_attempts_m2 = float(np.mean([t["attempts_used"] for t in all_turns_m2]))

    passed_guard_m1 = sum(1 for t in all_turns_m1 if t["evaluation"]["status"] == "Passed")
    passed_guard_m2 = sum(1 for t in all_turns_m2 if t["evaluation"]["status"] == "Passed")

    passed_eval_m1 = sum(1 for t in all_turns_m1 if t["evaluation"].get("eval_target_passed", False))
    passed_eval_m2 = sum(1 for t in all_turns_m2 if t["evaluation"].get("eval_target_passed", False))

    quarantine_count_m1 = 0
    quarantine_count_m2 = sum(1 for t in all_turns_m2 if not t["is_current"])
    quarantine_rate_m2 = (quarantine_count_m2 / len(all_turns_m2)) * 100.0

    same_m1 = np.mean([c["same_scene_continuity"] for c in m1["characters"].values()])
    same_m2 = np.mean([c["same_scene_continuity"] for c in m2["characters"].values()])

    trans_m1 = np.mean([c["transition_continuity"] for c in m1["characters"].values() if c["transition_continuity"] > 0]) if any(c["transition_continuity"] > 0 for c in m1["characters"].values()) else 0.0
    trans_m2 = np.mean([c["transition_continuity"] for c in m2["characters"].values() if c["transition_continuity"] > 0]) if any(c["transition_continuity"] > 0 for c in m2["characters"].values()) else 0.0

    action_m1 = np.mean([c["action_margin"] for c in m1["characters"].values()])
    action_m2 = np.mean([c["action_margin"] for c in m2["characters"].values()])

    print(f"{'Mean Face Similarity':<32} | {avg_face_m1:<22.4f} | {avg_face_m2:<18.4f}", flush=True)
    print(f"{'Median Face Similarity':<32} | {median_m1:<22.4f} | {median_m2:<18.4f}", flush=True)
    print(f"{'P10 Face Similarity':<32} | {p10_m1:<22.4f} | {p10_m2:<18.4f}", flush=True)
    print(f"{'Worst Face Score (Floor)':<32} | {worst_id_m1:<22.4f} | {worst_id_m2:<18.4f}", flush=True)
    print(f"{'Mean Feature Retention':<32} | {avg_feat_m1:<22.4f} | {avg_feat_m2:<18.4f}", flush=True)
    print(f"{'Guard Gate Passed (>=0.72)':<32} | {f'{passed_guard_m1}/{len(all_turns_m1)}':<22} | {f'{passed_guard_m2}/{len(all_turns_m2)}':<18}", flush=True)
    print(f"{'Eval Target Passed (>=0.75)':<32} | {f'{passed_eval_m1}/{len(all_turns_m1)}':<22} | {f'{passed_eval_m2}/{len(all_turns_m2)}':<18}", flush=True)
    print(f"{'Attempt 1 Pass Rate':<32} | {'100.0% (Unguarded)':<22} | {f'{(att1_pass_m2/len(all_turns_m2))*100.0:.1f}%':<18}", flush=True)
    print(f"{'Retry Trigger Rate':<32} | {'0.0% (Unguarded)':<22} | {f'{(retry_triggered_m2/len(all_turns_m2))*100.0:.1f}%':<18}", flush=True)
    print(f"{'Attempt 2/3 Recoveries':<32} | {'N/A':<22} | {f'{att2_rec_m2 + att3_rec_m2} frames':<18}", flush=True)
    print(f"{'Quarantined (Exhausted)':<32} | {'0 (Unguarded)':<22} | {f'{exhausted_m2} ({quarantine_rate_m2:.1f}%)':<18}", flush=True)
    print(f"{'Same-Scene Continuity':<32} | {same_m1:<22.4f} | {same_m2:<18.4f}", flush=True)
    print(f"{'Transition Continuity':<32} | {trans_m1:<22.4f} | {trans_m2:<18.4f}", flush=True)
    print(f"{'Action Margin (CLIP)':<32} | {action_m1:<22.4f} | {action_m2:<18.4f}", flush=True)
    print("="*80 + "\n", flush=True)

if __name__ == "__main__":
    stage = 1
    if "--stage" in sys.argv:
        idx = sys.argv.index("--stage")
        if idx + 1 < len(sys.argv):
            stage = int(sys.argv[idx + 1])
    
    max_turns = 4 if stage == 1 else 8
    run_benchmark(max_turns=max_turns)
