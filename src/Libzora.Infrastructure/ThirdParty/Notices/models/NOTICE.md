# Downloadable semantic-search models

Libzora does not bundle these model weights. At the user's request, the app
downloads the files listed below from Hugging Face and verifies their pinned
size or SHA-256 before installing them locally. The upstream licenses remain in
effect for every downloaded copy.

## BGE Small Chinese (`BAAI/bge-small-zh-v1.5`)

- Upstream model card: https://huggingface.co/BAAI/bge-small-zh-v1.5
- ONNX conversion: https://huggingface.co/Qdrant/bge-small-zh-v1.5
- Pinned conversion revision: `46fbe35fd4374a00fee7de77dfddaeb6dd6a2c59`
- License: MIT. The model card states that the released models can be used for
  commercial purposes free of charge.
- Authors / attribution: BAAI; ONNX conversion published by Qdrant.
- License text: [bge-small-zh-v1.5-MIT.txt](bge-small-zh-v1.5-MIT.txt)

## Multilingual E5 Small (`intfloat/multilingual-e5-small`)

- Upstream model card: https://huggingface.co/intfloat/multilingual-e5-small
- Official ONNX export: https://huggingface.co/intfloat/multilingual-e5-small/tree/main/onnx
- Pinned ONNX revision: `03415a4be176a1620747c692ed433219fabc3def`
- License: MIT, as declared on the upstream model card.
- Authors / attribution: intfloat (Liang Wang, Nan Yang, Xiaolong Huang,
  Linjun Yang, Rangan Majumder, Furu Wei).
- License text: [multilingual-e5-small-MIT.txt](multilingual-e5-small-MIT.txt)

These models produce numeric embeddings for local retrieval. The app does not
use them to generate or reproduce copyrighted text.
