"""
MIDSS AI Module - ONNX Model Testing Script
Test the exported ONNX model with sample images
"""

import os
import onnxruntime as ort
import numpy as np
from PIL import Image
import json
from pathlib import Path

# Configuration
MODEL_PATH = os.path.join('..', 'models', 'retinopathy_pretrained_v1.onnx')
IMAGE_SIZE = 512
CLASS_NAMES = ['No DR', 'Mild DR', 'Moderate DR', 'Severe DR', 'Proliferative DR']

# ImageNet normalization (same as training)
MEAN = np.array([0.485, 0.456, 0.406], dtype=np.float32)
STD = np.array([0.229, 0.224, 0.225], dtype=np.float32)

def preprocess_image(image_path):
    """
    Preprocess image for ONNX inference
    """
    # Load image
    img = Image.open(image_path).convert('RGB')
    
    # Resize
    img = img.resize((IMAGE_SIZE, IMAGE_SIZE))
    
    # Convert to numpy array [H, W, C]
    img_array = np.array(img, dtype=np.float32) / 255.0
    
    # Normalize
    img_array = (img_array - MEAN) / STD
    
    # Transpose to [C, H, W]
    img_array = img_array.transpose(2, 0, 1)
    
    # Add batch dimension [1, C, H, W]
    img_array = np.expand_dims(img_array, axis=0).astype(np.float32)
    
    return img_array

def softmax(x):
    """Compute softmax values"""
    exp_x = np.exp(x - np.max(x))
    return exp_x / exp_x.sum()

def predict(image_path, session):
    """
    Run inference on a single image
    """
    # Preprocess
    input_data = preprocess_image(image_path)
    
    # Run inference
    input_name = session.get_inputs()[0].name
    output_name = session.get_outputs()[0].name
    
    result = session.run([output_name], {input_name: input_data})
    
    # Get logits
    logits = result[0][0]
    
    # Apply softmax
    probabilities = softmax(logits)
    
    # Get prediction
    predicted_class = np.argmax(probabilities)
    confidence = probabilities[predicted_class]
    
    return predicted_class, confidence, probabilities

def main():
    print("=" * 60)
    print("MIDSS AI Module - ONNX Model Testing")
    print("=" * 60)
    
    # Check if model exists
    if not os.path.exists(MODEL_PATH):
        print(f"\n❌ Error: Model not found at {MODEL_PATH}")
        print("   Please run train_retinopathy.py first to create the model.")
        return
    
    print(f"\n✅ Model found: {MODEL_PATH}")
    print(f"   Size: {os.path.getsize(MODEL_PATH) / 1e6:.2f} MB")
    
    # Load ONNX model
    print("\n🧠 Loading ONNX model...")
    session = ort.InferenceSession(MODEL_PATH)
    
    # Print model info
    print(f"   Input name: {session.get_inputs()[0].name}")
    print(f"   Input shape: {session.get_inputs()[0].shape}")
    print(f"   Output name: {session.get_outputs()[0].name}")
    print(f"   Output shape: {session.get_outputs()[0].shape}")
    
    # Test with sample images
    print("\n🔍 Looking for test images...")
    test_images_dir = os.path.join('..', 'data', 'raw', 'train_images')
    
    if not os.path.exists(test_images_dir):
        print(f"❌ Test images directory not found: {test_images_dir}")
        print("   Please download APTOS dataset first.")
        return
    
    # Get first 5 images as samples
    image_files = list(Path(test_images_dir).glob('*.png'))[:5]
    
    if not image_files:
        print("❌ No PNG images found in test directory")
        return
    
    print(f"   Found {len(image_files)} test images")
    print("\n" + "=" * 60)
    print("Running inference on sample images...")
    print("=" * 60)
    
    # Run predictions
    for idx, img_path in enumerate(image_files, 1):
        print(f"\n📸 Image {idx}: {img_path.name}")
        
        try:
            pred_class, confidence, probabilities = predict(str(img_path), session)
            
            print(f"   Prediction: {CLASS_NAMES[pred_class]}")
            print(f"   Confidence: {confidence*100:.2f}%")
            print(f"   All probabilities:")
            for i, prob in enumerate(probabilities):
                print(f"      {CLASS_NAMES[i]}: {prob*100:.2f}%")
        
        except Exception as e:
            print(f"   ❌ Error processing image: {e}")
    
    print("\n" + "=" * 60)
    print("✅ Testing completed!")
    print("\nNext steps:")
    print("1. Copy retinopathy_pretrained_v1.onnx to MIDSS API project")
    print("2. Implement AIInferenceService in backend")
    print("3. Test with real DICOM studies")
    print("=" * 60)

if __name__ == '__main__':
    main()
