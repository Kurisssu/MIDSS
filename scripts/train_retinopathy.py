"""
SITIM AI Module - Diabetic Retinopathy Training Script
Training ResNet18 on APTOS 2019 dataset for 5-class classification
"""

import os
import pandas as pd
import torch
import torch.nn as nn
import torch.optim as optim
from torch.utils.data import Dataset, DataLoader
from torchvision import models, transforms
from PIL import Image
from sklearn.model_selection import train_test_split
from sklearn.metrics import classification_report, confusion_matrix
import numpy as np
from tqdm import tqdm
import json
from datetime import datetime

# ==================== Configuration ====================
class Config:
    # Paths
    DATA_DIR = os.path.join('..', 'data', 'raw')
    TRAIN_IMAGES_DIR = os.path.join(DATA_DIR, 'train_images')
    TRAIN_CSV = os.path.join(DATA_DIR, 'train.csv')
    MODEL_OUTPUT_DIR = os.path.join('..', 'models')
    
    # Training parameters
    IMAGE_SIZE = 512
    BATCH_SIZE = 16  # Reduce to 8 if out of memory
    NUM_EPOCHS = 10
    LEARNING_RATE = 0.0001
    NUM_CLASSES = 5  # 0=No DR, 1=Mild, 2=Moderate, 3=Severe, 4=Proliferative
    VALIDATION_SPLIT = 0.2
    
    # Model
    MODEL_NAME = 'resnet18'
    PRETRAINED = True
    
    # Device
    DEVICE = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    
    # Random seed for reproducibility
    SEED = 42

# ==================== Dataset Class ====================
class RetinopathyDataset(Dataset):
    def __init__(self, dataframe, image_dir, transform=None):
        self.df = dataframe.reset_index(drop=True)
        self.image_dir = image_dir
        self.transform = transform
    
    def __len__(self):
        return len(self.df)
    
    def __getitem__(self, idx):
        img_name = self.df.loc[idx, 'id_code'] + '.png'
        img_path = os.path.join(self.image_dir, img_name)
        
        # Load image
        image = Image.open(img_path).convert('RGB')
        
        # Apply transforms
        if self.transform:
            image = self.transform(image)
        
        # Get label
        label = int(self.df.loc[idx, 'diagnosis'])
        
        return image, label

# ==================== Data Transforms ====================
def get_transforms():
    train_transform = transforms.Compose([
        transforms.Resize((Config.IMAGE_SIZE, Config.IMAGE_SIZE)),
        transforms.RandomHorizontalFlip(p=0.5),
        transforms.RandomRotation(degrees=15),
        transforms.ColorJitter(brightness=0.2, contrast=0.2),
        transforms.ToTensor(),
        transforms.Normalize(mean=[0.485, 0.456, 0.406], 
                           std=[0.229, 0.224, 0.225])
    ])
    
    val_transform = transforms.Compose([
        transforms.Resize((Config.IMAGE_SIZE, Config.IMAGE_SIZE)),
        transforms.ToTensor(),
        transforms.Normalize(mean=[0.485, 0.456, 0.406], 
                           std=[0.229, 0.224, 0.225])
    ])
    
    return train_transform, val_transform

# ==================== Model ====================
def create_model():
    # Use weights parameter instead of deprecated pretrained
    if Config.PRETRAINED:
        model = models.resnet18(weights=models.ResNet18_Weights.IMAGENET1K_V1)
    else:
        model = models.resnet18(weights=None)
    
    # Replace final layer for 5 classes
    num_features = model.fc.in_features
    model.fc = nn.Linear(num_features, Config.NUM_CLASSES)
    
    return model.to(Config.DEVICE)

# ==================== Training ====================
def train_epoch(model, dataloader, criterion, optimizer, device):
    model.train()
    running_loss = 0.0
    correct = 0
    total = 0
    
    pbar = tqdm(dataloader, desc='Training')
    for images, labels in pbar:
        images, labels = images.to(device), labels.to(device)
        
        # Forward pass
        optimizer.zero_grad()
        outputs = model(images)
        loss = criterion(outputs, labels)
        
        # Backward pass
        loss.backward()
        optimizer.step()
        
        # Statistics
        running_loss += loss.item() * images.size(0)
        _, predicted = outputs.max(1)
        total += labels.size(0)
        correct += predicted.eq(labels).sum().item()
        
        # Update progress bar
        pbar.set_postfix({
            'loss': f'{loss.item():.4f}',
            'acc': f'{100.*correct/total:.2f}%'
        })
    
    epoch_loss = running_loss / total
    epoch_acc = correct / total
    
    return epoch_loss, epoch_acc

# ==================== Validation ====================
def validate(model, dataloader, criterion, device):
    model.eval()
    running_loss = 0.0
    correct = 0
    total = 0
    all_preds = []
    all_labels = []
    
    with torch.no_grad():
        pbar = tqdm(dataloader, desc='Validation')
        for images, labels in pbar:
            images, labels = images.to(device), labels.to(device)
            
            outputs = model(images)
            loss = criterion(outputs, labels)
            
            running_loss += loss.item() * images.size(0)
            _, predicted = outputs.max(1)
            total += labels.size(0)
            correct += predicted.eq(labels).sum().item()
            
            all_preds.extend(predicted.cpu().numpy())
            all_labels.extend(labels.cpu().numpy())
            
            pbar.set_postfix({
                'loss': f'{loss.item():.4f}',
                'acc': f'{100.*correct/total:.2f}%'
            })
    
    epoch_loss = running_loss / total
    epoch_acc = correct / total
    
    return epoch_loss, epoch_acc, all_preds, all_labels

# ==================== Main Training Loop ====================
def main():
    print("=" * 60)
    print("SITIM AI Module - Diabetic Retinopathy Training")
    print("=" * 60)
    
    # Set random seed
    torch.manual_seed(Config.SEED)
    np.random.seed(Config.SEED)
    
    # Device info
    print(f"\n🖥️  Device: {Config.DEVICE}")
    if Config.DEVICE.type == 'cuda':
        print(f"   GPU: {torch.cuda.get_device_name(0)}")
        print(f"   Memory: {torch.cuda.get_device_properties(0).total_memory / 1e9:.2f} GB")
    
    # Load data
    print(f"\n📂 Loading dataset from {Config.TRAIN_CSV}")
    df = pd.read_csv(Config.TRAIN_CSV)
    print(f"   Total images: {len(df)}")
    print(f"   Class distribution:")
    print(df['diagnosis'].value_counts().sort_index())
    
    # Train/Val split
    train_df, val_df = train_test_split(
        df, 
        test_size=Config.VALIDATION_SPLIT, 
        stratify=df['diagnosis'],
        random_state=Config.SEED
    )
    print(f"\n📊 Split:")
    print(f"   Training: {len(train_df)} images")
    print(f"   Validation: {len(val_df)} images")
    
    # Transforms
    train_transform, val_transform = get_transforms()
    
    # Datasets
    train_dataset = RetinopathyDataset(train_df, Config.TRAIN_IMAGES_DIR, train_transform)
    val_dataset = RetinopathyDataset(val_df, Config.TRAIN_IMAGES_DIR, val_transform)
    
    # DataLoaders
    train_loader = DataLoader(
        train_dataset, 
        batch_size=Config.BATCH_SIZE, 
        shuffle=True, 
        num_workers=4,
        pin_memory=True if Config.DEVICE.type == 'cuda' else False
    )
    val_loader = DataLoader(
        val_dataset, 
        batch_size=Config.BATCH_SIZE, 
        shuffle=False, 
        num_workers=4,
        pin_memory=True if Config.DEVICE.type == 'cuda' else False
    )
    
    # Model
    print(f"\n🧠 Creating model: {Config.MODEL_NAME}")
    model = create_model()
    print(f"   Total parameters: {sum(p.numel() for p in model.parameters()):,}")
    print(f"   Trainable parameters: {sum(p.numel() for p in model.parameters() if p.requires_grad):,}")
    
    # Loss and optimizer
    criterion = nn.CrossEntropyLoss()
    optimizer = optim.Adam(model.parameters(), lr=Config.LEARNING_RATE)
    
    # Learning rate scheduler
    scheduler = optim.lr_scheduler.ReduceLROnPlateau(
        optimizer, mode='min', factor=0.5, patience=2
    )
    
    # Training loop
    print(f"\n🚀 Starting training for {Config.NUM_EPOCHS} epochs")
    print("=" * 60)
    
    best_val_acc = 0.0
    history = {
        'train_loss': [], 'train_acc': [],
        'val_loss': [], 'val_acc': []
    }
    
    for epoch in range(Config.NUM_EPOCHS):
        print(f"\nEpoch {epoch+1}/{Config.NUM_EPOCHS}")
        print("-" * 60)
        
        # Train
        train_loss, train_acc = train_epoch(
            model, train_loader, criterion, optimizer, Config.DEVICE
        )
        
        # Validate
        val_loss, val_acc, val_preds, val_labels = validate(
            model, val_loader, criterion, Config.DEVICE
        )
        
        # Update scheduler
        scheduler.step(val_loss)
        
        # Save history
        history['train_loss'].append(train_loss)
        history['train_acc'].append(train_acc)
        history['val_loss'].append(val_loss)
        history['val_acc'].append(val_acc)
        
        # Print summary
        print(f"\n📈 Epoch {epoch+1} Summary:")
        print(f"   Train Loss: {train_loss:.4f} | Train Acc: {train_acc*100:.2f}%")
        print(f"   Val Loss:   {val_loss:.4f} | Val Acc:   {val_acc*100:.2f}%")
        
        # Save best model
        if val_acc > best_val_acc:
            best_val_acc = val_acc
            best_model_path = os.path.join(
                Config.MODEL_OUTPUT_DIR, 
                'retinopathy_best.pth'
            )
            torch.save({
                'epoch': epoch,
                'model_state_dict': model.state_dict(),
                'optimizer_state_dict': optimizer.state_dict(),
                'val_acc': val_acc,
                'val_loss': val_loss,
            }, best_model_path)
            print(f"   ✅ Best model saved! (Val Acc: {val_acc*100:.2f}%)")
    
    print("\n" + "=" * 60)
    print("🎉 Training completed!")
    print(f"   Best validation accuracy: {best_val_acc*100:.2f}%")
    
    # Final evaluation
    print("\n📊 Final Validation Metrics:")
    print("\nClassification Report:")
    class_names = ['No DR', 'Mild', 'Moderate', 'Severe', 'Proliferative']
    print(classification_report(val_labels, val_preds, target_names=class_names))
    
    print("\nConfusion Matrix:")
    cm = confusion_matrix(val_labels, val_preds)
    print(cm)
    
    # Export to ONNX
    print("\n📦 Exporting model to ONNX...")
    model.eval()
    dummy_input = torch.randn(1, 3, Config.IMAGE_SIZE, Config.IMAGE_SIZE).to(Config.DEVICE)
    onnx_path = os.path.join(Config.MODEL_OUTPUT_DIR, 'retinopathy_pretrained_v1.onnx')
    
    torch.onnx.export(
        model,
        dummy_input,
        onnx_path,
        export_params=True,
        opset_version=14,
        do_constant_folding=True,
        input_names=['input'],
        output_names=['output'],
        dynamic_axes={
            'input': {0: 'batch_size'},
            'output': {0: 'batch_size'}
        }
    )
    
    print(f"   ✅ ONNX model saved: {onnx_path}")
    print(f"   File size: {os.path.getsize(onnx_path) / 1e6:.2f} MB")
    
    # Save training report
    report = {
        'training_date': datetime.now().isoformat(),
        'model': Config.MODEL_NAME,
        'num_epochs': Config.NUM_EPOCHS,
        'best_val_accuracy': float(best_val_acc),
        'final_train_loss': float(history['train_loss'][-1]),
        'final_val_loss': float(history['val_loss'][-1]),
        'num_train_samples': len(train_df),
        'num_val_samples': len(val_df),
        'image_size': Config.IMAGE_SIZE,
        'batch_size': Config.BATCH_SIZE,
        'learning_rate': Config.LEARNING_RATE,
        'device': str(Config.DEVICE),
        'class_names': class_names,
        'confusion_matrix': cm.tolist(),
        'history': history
    }
    
    report_path = os.path.join(Config.MODEL_OUTPUT_DIR, 'training_report.json')
    with open(report_path, 'w') as f:
        json.dump(report, f, indent=2)
    
    print(f"   ✅ Training report saved: {report_path}")
    
    print("\n" + "=" * 60)
    print("✅ All done! Model ready for deployment in SITIM.")
    print("=" * 60)

if __name__ == '__main__':
    # Create output directory if not exists
    os.makedirs(Config.MODEL_OUTPUT_DIR, exist_ok=True)
    
    # Run training
    main()
