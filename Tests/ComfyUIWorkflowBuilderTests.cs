using Application.Interfaces;
using Infrastructure.ImageGeneration.ComfyUI;
using Xunit;

namespace Tests;

public sealed class ComfyUIWorkflowBuilderTests
{
    [Fact]
    public void VisualIdentityWorkflowV1Builder_Builds_Exact_Node_Graph_With_Frozen_Parameters()
    {
        var builder = new VisualIdentityWorkflowV1Builder();

        Assert.Equal("VisualIdentity", builder.WorkflowName);
        Assert.Equal(1, builder.WorkflowVersion);

        var request = new ImageGenerationRequest(
            Prompt: "masterpiece, 1girl, silver hair, red eyes, dragon horns",
            NegativePrompt: "bad anatomy, blurry",
            Width: 512,
            Height: 768,
            Steps: 30,
            GuidanceScale: 7.0f,
            Seed: 123456789,
            Model: "meinamix_meinaV11.safetensors",
            Sampler: "euler_ancestral",
            Scheduler: "karras",
            Workflow: "VisualIdentity",
            WorkflowVersion: 1,
            ParametersJson: "{\"ipAdapter\":{\"weight\":0.45,\"endAt\":0.70}}"
        );

        var graph = builder.BuildWorkflow(request, "character_canonical_ref.png");

        Assert.NotNull(graph);
        Assert.Equal(11, graph.Count);

        // Node 1: LoadImage
        Assert.True(graph.ContainsKey("1"));
        var node1 = Assert.IsAssignableFrom<Dictionary<string, object>>(graph["1"]);
        Assert.Equal("LoadImage", node1["class_type"]);
        var node1Inputs = Assert.IsAssignableFrom<Dictionary<string, object>>(node1["inputs"]);
        Assert.Equal("character_canonical_ref.png", node1Inputs["image"]);

        // Node 2: CLIPVisionLoader
        Assert.True(graph.ContainsKey("2"));
        var node2 = Assert.IsAssignableFrom<Dictionary<string, object>>(graph["2"]);
        Assert.Equal("CLIPVisionLoader", node2["class_type"]);

        // Node 8: IPAdapterModelLoader
        Assert.True(graph.ContainsKey("8"));
        var node8 = Assert.IsAssignableFrom<Dictionary<string, object>>(graph["8"]);
        Assert.Equal("IPAdapterModelLoader", node8["class_type"]);

        // Node 10: IPAdapterAdvanced
        Assert.True(graph.ContainsKey("10"));
        var node10 = Assert.IsAssignableFrom<Dictionary<string, object>>(graph["10"]);
        Assert.Equal("IPAdapterAdvanced", node10["class_type"]);
        var node10Inputs = Assert.IsAssignableFrom<Dictionary<string, object>>(node10["inputs"]);
        Assert.Equal(0.45, (double)node10Inputs["weight"], precision: 2);
        Assert.Equal(0.70, (double)node10Inputs["end_at"], precision: 2);
        Assert.Equal("linear", node10Inputs["weight_type"]);
        Assert.Equal("K+V", node10Inputs["embeds_scaling"]);

        // Node 4: CheckpointLoaderSimple
        Assert.True(graph.ContainsKey("4"));
        var node4 = Assert.IsAssignableFrom<Dictionary<string, object>>(graph["4"]);
        Assert.Equal("CheckpointLoaderSimple", node4["class_type"]);
        var node4Inputs = Assert.IsAssignableFrom<Dictionary<string, object>>(node4["inputs"]);
        Assert.Equal("meinamix_meinaV11.safetensors", node4Inputs["ckpt_name"]);

        // Node 5: EmptyLatentImage
        Assert.True(graph.ContainsKey("5"));
        var node5 = Assert.IsAssignableFrom<Dictionary<string, object>>(graph["5"]);
        var node5Inputs = Assert.IsAssignableFrom<Dictionary<string, object>>(node5["inputs"]);
        Assert.Equal(512, node5Inputs["width"]);
        Assert.Equal(768, node5Inputs["height"]);

        // Node 6: Positive Prompt
        Assert.True(graph.ContainsKey("6"));
        var node6 = Assert.IsAssignableFrom<Dictionary<string, object>>(graph["6"]);
        var node6Inputs = Assert.IsAssignableFrom<Dictionary<string, object>>(node6["inputs"]);
        Assert.Equal("masterpiece, 1girl, silver hair, red eyes, dragon horns", node6Inputs["text"]);

        // Node 7: Negative Prompt
        Assert.True(graph.ContainsKey("7"));
        var node7 = Assert.IsAssignableFrom<Dictionary<string, object>>(graph["7"]);
        var node7Inputs = Assert.IsAssignableFrom<Dictionary<string, object>>(node7["inputs"]);
        Assert.Equal("bad anatomy, blurry", node7Inputs["text"]);

        // Node 3: KSampler
        Assert.True(graph.ContainsKey("3"));
        var node3 = Assert.IsAssignableFrom<Dictionary<string, object>>(graph["3"]);
        Assert.Equal("KSampler", node3["class_type"]);
        var node3Inputs = Assert.IsAssignableFrom<Dictionary<string, object>>(node3["inputs"]);
        Assert.Equal(123456789L, node3Inputs["seed"]);
        Assert.Equal(30, node3Inputs["steps"]);
        Assert.Equal(7.0, (double)node3Inputs["cfg"], precision: 2);
        Assert.Equal("euler_ancestral", node3Inputs["sampler_name"]);
        Assert.Equal("karras", node3Inputs["scheduler"]);

        // Node 11: SaveImage
        Assert.True(graph.ContainsKey("11"));
        var node11 = Assert.IsAssignableFrom<Dictionary<string, object>>(graph["11"]);
        var node11Inputs = Assert.IsAssignableFrom<Dictionary<string, object>>(node11["inputs"]);
        Assert.Equal("VisualIdentity_v1", node11Inputs["filename_prefix"]);
    }

    [Fact]
    public void VisualIdentityWorkflowV1Builder_Respects_Custom_IPAdapter_Overrides()
    {
        var builder = new VisualIdentityWorkflowV1Builder();

        var request = new ImageGenerationRequest(
            Prompt: "solo, 1girl",
            ParametersJson: "{\"ipAdapter\":{\"weight\":0.55,\"endAt\":0.85}}",
            Seed: 42
        );

        var graph = builder.BuildWorkflow(request, "custom_avatar.png");
        var node10 = Assert.IsAssignableFrom<Dictionary<string, object>>(graph["10"]);
        var node10Inputs = Assert.IsAssignableFrom<Dictionary<string, object>>(node10["inputs"]);

        Assert.Equal(0.55, (double)node10Inputs["weight"], precision: 2);
        Assert.Equal(0.85, (double)node10Inputs["end_at"], precision: 2);
    }

    [Fact]
    public void VisualIdentityWorkflowV1Builder_GraphTopology_HasExactRequiredNodeConnections()
    {
        var builder = new VisualIdentityWorkflowV1Builder();
        var request = new ImageGenerationRequest(Prompt: "masterpiece, 1girl", Seed: 42);
        var graph = builder.BuildWorkflow(request, "ref_face.png");

        // 1. IPAdapter (Node 10) MUST receive Checkpoint Model (Node 4), IPAdapter Model (Node 8), Ref Image (Node 1), CLIP Vision (Node 2)
        var ipAdapter = (Dictionary<string, object>)((Dictionary<string, object>)graph["10"])["inputs"];
        Assert.Equal(new object[] { "4", 0 }, ipAdapter["model"]);
        Assert.Equal(new object[] { "8", 0 }, ipAdapter["ipadapter"]);
        Assert.Equal(new object[] { "1", 0 }, ipAdapter["image"]);
        Assert.Equal(new object[] { "2", 0 }, ipAdapter["clip_vision"]);

        // 2. KSampler (Node 3) MUST receive IPAdapter Advanced output (Node 10, index 0), NOT raw checkpoint model!
        var ksampler = (Dictionary<string, object>)((Dictionary<string, object>)graph["3"])["inputs"];
        Assert.Equal(new object[] { "10", 0 }, ksampler["model"]);
        Assert.Equal(new object[] { "6", 0 }, ksampler["positive"]);
        Assert.Equal(new object[] { "7", 0 }, ksampler["negative"]);
        Assert.Equal(new object[] { "5", 0 }, ksampler["latent_image"]);

        // 3. VAE Decode (Node 9) MUST receive KSampler Latent (Node 3) and Checkpoint VAE (Node 4, index 2)
        var vaeDecode = (Dictionary<string, object>)((Dictionary<string, object>)graph["9"])["inputs"];
        Assert.Equal(new object[] { "3", 0 }, vaeDecode["samples"]);
        Assert.Equal(new object[] { "4", 2 }, vaeDecode["vae"]);

        // 4. SaveImage (Node 11) MUST receive VAE Decode image (Node 9, index 0)
        var saveImage = (Dictionary<string, object>)((Dictionary<string, object>)graph["11"])["inputs"];
        Assert.Equal(new object[] { "9", 0 }, saveImage["images"]);
    }

    [Fact]
    public void VisualIdentityWorkflowV1Builder_Uses_Calibrated_Default_Parameters_When_ParametersJson_Is_Null()
    {
        var builder = new VisualIdentityWorkflowV1Builder();
        var request = new ImageGenerationRequest(
            Prompt: "masterpiece, 1girl, silver hair, red eyes",
            Seed: 987654321,
            ParametersJson: null
        );

        var graph = builder.BuildWorkflow(request, "ref.png");
        var node10 = Assert.IsAssignableFrom<Dictionary<string, object>>(graph["10"]);
        var inputs = Assert.IsAssignableFrom<Dictionary<string, object>>(node10["inputs"]);

        Assert.Equal(0.65, (double)inputs["weight"], precision: 2);
        Assert.Equal(0.85, (double)inputs["end_at"], precision: 2);
        Assert.Equal("K+V", inputs["embeds_scaling"]);
    }
}
