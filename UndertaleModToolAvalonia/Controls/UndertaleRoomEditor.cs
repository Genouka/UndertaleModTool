using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Xaml.Interactions.DragAndDrop;
using Avalonia.Xaml.Interactivity;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using static UndertaleModLib.Models.UndertaleRoom;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Interactive room editor canvas.
///
/// Rendering mirrors the WPF "UndertaleRoomEditor": the room is drawn as a flat list of draw
/// items (<see cref="SceneItem"/>) whose transform math replicates the WPF control's XAML
/// templates (objects positioned by their sprite origin, scaled/rotated around it, backgrounds
/// tiled from their offset, GMS2 tilemaps drawn per tile with the GameMaker orientation flags).
///
/// Unlike the previous implementation, the scene is a retained structure: it is only rebuilt
/// when the room data actually changes, only items intersecting the viewport are drawn, and
/// repaints happen on demand (input, selection, property changes, image loads) instead of in an
/// endless 60 fps loop. Large rooms therefore pan/zoom without any per-frame rebuild.
/// </summary>
public class UndertaleRoomEditor : Control
{
    enum InteractionMode
    {
        Items,
        Tiles,
        RoomTiles,
    }

    UndertaleRoomViewModel? vm;

    RoomScene? scene;
    bool sceneDirty = true;
    bool pendingImages;
    int lastStructureSignature = int.MinValue;

    // Room controls
    internal Vector translation = new(0, 0);
    internal double scaling = 1;

    bool translationMoving = false;
    bool translationHasMoved = false;
    Vector translationMoveOffset = new(0, 0);

    Point pointerPosition;
    Point pointerPositionInRoom;
    KeyModifiers lastKeyModifiers = KeyModifiers.None;

    SceneItem? dragItem;
    Vector dragHotpoint = new(0, 0);
    bool dragLeft, dragRight, dragTop, dragBottom;
    Point dragObjectOrigin;

    object? hoveredObject;
    uint? hoveredTile = null;

    bool paintingTiles;
    readonly HashSet<(int x, int y)> paintedCells = new();

    readonly Stack<MoveSnapshot> undoStack = new();
    readonly record struct MoveSnapshot(UndertaleObject Object, int X, int Y, float ScaleX, float ScaleY);

    INotifyPropertyChanged? propertiesContentSubscribed;

    #region Touch

    enum TouchMode
    {
        None,
        PossibleTap,
        Panning,
        MovingItem,
        Pinching,
    }

    static readonly TimeSpan TouchLongPressDuration = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan TouchTwoFingerTapDuration = TimeSpan.FromMilliseconds(700);
    const double TouchMoveThreshold = 12;
    const double TouchPinchStartDistanceThreshold = 8;

    readonly Dictionary<long, Point> touchPoints = new();
    TouchMode touchMode = TouchMode.None;
    long? touchPrimaryId = null;
    long? touchSecondaryId = null;
    Point touchStartPosition;
    Point touchSecondStartPosition;
    double touchTwoFingerStartDistance;
    DateTime touchTwoFingerStartTime;
    bool touchTwoFingerCancelled;
    DispatcherTimer? longPressTimer;

    double pinchStartDistance;
    double pinchStartScale;
    Point pinchStartRoomPoint;

    #endregion

    public UndertaleRoomEditor()
    {
        ClipToBounds = true;
        Focusable = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);

        Interaction.SetBehaviors(this, [new ContextDropBehavior() { Handler = new UndertaleReferenceDropHandler() }]);
    }

    #region VM wiring

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        DetachVM();
        AttachVM(DataContext as UndertaleRoomViewModel);
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Re-attach after being detached (e.g. when switching between tabs keeps the view alive).
        if (vm is null && DataContext is UndertaleRoomViewModel newVM)
            AttachVM(newVM);
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachVM();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BoundsProperty)
            InvalidateVisual();
    }

    void AttachVM(UndertaleRoomViewModel? newVM)
    {
        vm = newVM;
        if (vm is null)
            return;

        vm.Room.SetupRoom();

        // Warm the texture page cache in the background so entering the room doesn't stall the UI
        // thread; images pop in as they finish decoding (ImageLoaded triggers a repaint).
        _ = vm.MainVM.ImageCache.PreloadAsync(vm.Room);

        translation = new(0, 0);
        scaling = vm.Zoom;
        sceneDirty = true;
        scene = null;
        undoStack.Clear();
        ResetDragState();

        vm.PropertyChanged += VmPropertyChanged;
        vm.Room.PropertyChanged += RoomPropertyChanged;
        vm.MainVM.ImageCache.ImageLoaded += ImageCacheImageLoaded;
        AttachPropertiesContent(vm.PropertiesContent);
    }

    void DetachVM()
    {
        if (vm is null)
            return;

        vm.PropertyChanged -= VmPropertyChanged;
        vm.Room.PropertyChanged -= RoomPropertyChanged;
        vm.MainVM.ImageCache.ImageLoaded -= ImageCacheImageLoaded;
        AttachPropertiesContent(null);

        vm = null;
        scene = null;
    }

    void AttachPropertiesContent(object? content)
    {
        if (ReferenceEquals(propertiesContentSubscribed, content))
            return;

        if (propertiesContentSubscribed is not null)
            propertiesContentSubscribed.PropertyChanged -= PropertiesContentPropertyChanged;

        propertiesContentSubscribed = content as INotifyPropertyChanged;

        if (propertiesContentSubscribed is not null)
            propertiesContentSubscribed.PropertyChanged += PropertiesContentPropertyChanged;
    }

    void VmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (vm is null)
            return;

        switch (e.PropertyName)
        {
            case nameof(UndertaleRoomViewModel.Zoom):
                // Zoom changed externally (slider): keep the control center anchored.
                if (Math.Abs(vm.Zoom - scaling) > 0.000001)
                {
                    Point center = new(Bounds.Width / 2, Bounds.Height / 2);
                    double factor = vm.Zoom / scaling;
                    if (double.IsFinite(factor) && factor > 0)
                    {
                        translation = center - (center - translation) * factor;
                        scaling = vm.Zoom;
                    }
                }
                InvalidateVisual();
                break;

            case nameof(UndertaleRoomViewModel.PropertiesContent):
                AttachPropertiesContent(vm.PropertiesContent);
                sceneDirty = true;
                InvalidateVisual();
                break;

            case nameof(UndertaleRoomViewModel.RoomTreeItemsSelectedItem):
            case nameof(UndertaleRoomViewModel.CategorySelected):
            case nameof(UndertaleRoomViewModel.GridWidth):
            case nameof(UndertaleRoomViewModel.GridHeight):
            case nameof(UndertaleRoomViewModel.IsGridEnabled):
            case nameof(UndertaleRoomViewModel.IsLocked):
            case nameof(UndertaleRoomViewModel.IsSelectAnyLayerEnabled):
            case nameof(UndertaleRoomViewModel.SelectedTileData):
            case nameof(UndertaleRoomViewModel.SelectedTileResource):
            case nameof(UndertaleRoomViewModel.SelectedTileSourceRect):
                InvalidateVisual();
                break;
        }
    }

    void RoomPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        sceneDirty = true;
        InvalidateVisual();
    }

    void PropertiesContentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        sceneDirty = true;
        InvalidateVisual();
    }

    void ImageCacheImageLoaded()
    {
        InvalidateVisual();
    }

    #endregion

    #region Scene

    List<SceneItem> SceneItems()
    {
        EnsureScene();
        return scene!.Items;
    }

    void EnsureScene()
    {
        if (vm is null)
            return;

        if (scene is not null && !sceneDirty)
        {
            if (ComputeStructureSignature() == lastStructureSignature)
                return;
        }

        scene = RoomScene.Build(vm, out pendingImages);
        sceneDirty = pendingImages; // rebuild again once the missing images finished decoding
        lastStructureSignature = ComputeStructureSignature();
    }

    // Cheap change detector for mutations that don't raise change notifications (tree context
    // menus add/remove objects, tile data gets reimported, layer offsets edited in the panel).
    int ComputeStructureSignature()
    {
        if (vm is null)
            return 0;

        UndertaleRoom room = vm.Room;

        HashCode hash = new();
        hash.Add(room.Width);
        hash.Add(room.Height);
        hash.Add(room.GameObjects.Count);
        hash.Add(room.Tiles.Count);

        foreach (UndertaleRoom.Background bg in room.Backgrounds)
        {
            hash.Add(bg.Enabled);
            hash.Add(bg.BackgroundDefinition is not null);
        }

        foreach (UndertaleRoom.Layer layer in room.Layers)
        {
            hash.Add(layer.IsVisible);
            hash.Add((int)layer.LayerType);
            hash.Add(layer.LayerDepth);
            hash.Add(layer.XOffset);
            hash.Add(layer.YOffset);
            hash.Add(layer.InstancesData?.Instances.Count ?? -1);
            hash.Add(layer.AssetsData?.LegacyTiles?.Count ?? -1);
            hash.Add(layer.AssetsData?.Sprites?.Count ?? -1);
            hash.Add(layer.AssetsData?.ParticleSystems?.Count ?? -1);
            if (layer.TilesData is { } tilesData)
            {
                hash.Add(tilesData.TilesX);
                hash.Add(tilesData.TilesY);
                hash.Add(RuntimeHelpers.GetHashCode(tilesData.TileData));
            }
            else
            {
                hash.Add(-2);
            }
        }

        return hash.ToHashCode();
    }

    #endregion

    #region Rendering

    public override void Render(DrawingContext context)
    {
        if (vm is null || !IsEffectivelyVisible)
            return;

        EnsureScene();
        RoomScene currentScene = scene!;

        Stopwatch? stopWatch = null;
        if (vm.MainVM.Settings?.ShowRoomEditorDebugInfo == true)
        {
            stopWatch = new Stopwatch();
            stopWatch.Start();
        }

        uint roomWidth = vm.Room.Width;
        uint roomHeight = vm.Room.Height;

        context.FillRectangle(RoomEditorAssets.BackdropBrush, new Rect(0, 0, Bounds.Width, Bounds.Height));
        context.DrawRectangle(null, RoomEditorAssets.RoomBorderPen, new Rect(translation.X - 1, translation.Y - 1,
            Math.Ceiling(roomWidth * scaling + 1), Math.Ceiling(roomHeight * scaling + 1)));

        Rect viewport = new(-translation.X / scaling, -translation.Y / scaling,
            Bounds.Width / scaling, Bounds.Height / scaling);

        // Room coords -> screen: scale first, then translate.
        using (context.PushTransform(Matrix.CreateScale(scaling, scaling) * Matrix.CreateTranslation(translation.X, translation.Y)))
        {
            // Room background color (GMS1 "BackgroundColor", GMS2 "BGColorLayer.BackgroundData.Color").
            context.FillRectangle(currentScene.BackgroundBrush, new Rect(0, 0, roomWidth, roomHeight));

            foreach (SceneItem item in currentScene.Items)
            {
                if (item.Valid && item.Bounds.Intersects(viewport))
                    item.Draw(context, viewport);
            }

            DrawGrid(context, viewport);

            SceneItem? selectedItem = currentScene.FindItem(vm.RoomTreeItemsSelectedItem);
            SceneItem? hoveredItem = currentScene.FindItem(hoveredObject);

            Color selectionColor = GetSelectionColor();
            if (hoveredItem is not null && hoveredItem != selectedItem)
                DrawItemOutline(context, hoveredItem, new Pen(new SolidColorBrush(selectionColor), 1 / scaling));
            if (selectedItem is not null)
                DrawItemOutline(context, selectedItem, new Pen(new SolidColorBrush(selectionColor), 2 / scaling));
        }

        if (stopWatch is not null)
        {
            stopWatch.Stop();
            RenderDebugText(context, Math.Ceiling(stopWatch.Elapsed.TotalMilliseconds));
        }
    }

    Color GetSelectionColor()
    {
        Color color = this.GetSolidColorBrushResource("SystemControlHighlightAccentBrush").Color;
        return new Color(128, color.R, color.G, color.B);
    }

    static void DrawItemOutline(DrawingContext context, SceneItem item, Pen pen)
    {
        Point[] corners = item.OutlineCorners();
        for (int i = 0; i < 4; i++)
            context.DrawLine(pen, corners[i], corners[(i + 1) & 3]);
    }

    void DrawGrid(DrawingContext context, Rect viewport)
    {
        if (vm is null || !vm.IsGridEnabled)
            return;

        double gridWidth = Math.Max((double)vm.GridWidth, 1);
        double gridHeight = Math.Max((double)vm.GridHeight, 1);

        if (gridWidth * scaling < 2.5 || gridHeight * scaling < 2.5)
            return; // avoid drawing an unreadable moiré at low zoom

        uint roomWidth = vm.Room.Width;
        uint roomHeight = vm.Room.Height;
        Pen pen = new(RoomEditorAssets.GridBrush, 1 / scaling);

        double left = Math.Max(viewport.X, 0);
        double right = Math.Min(viewport.Right, roomWidth);
        double top = Math.Max(viewport.Y, 0);
        double bottom = Math.Min(viewport.Bottom, roomHeight);
        if (left > right || top > bottom)
            return;

        for (double x = Math.Ceiling(left / gridWidth) * gridWidth; x <= right; x += gridWidth)
            context.DrawLine(pen, new Point(x, top), new Point(x, bottom));

        for (double y = Math.Ceiling(top / gridHeight) * gridHeight; y <= bottom; y += gridHeight)
            context.DrawLine(pen, new Point(left, y), new Point(right, y));
    }

    void RenderDebugText(DrawingContext context, double renderTimeMs)
    {
        static string GetTileInfo(uint? tile)
        {
            if (tile is uint tileNN)
            {
                uint tileId = tileNN & UndertaleRoomViewModel.TILE_ID;
                uint tileOrientation = tileNN >> 28;

                float scaleX = (((tileOrientation >> 0) & 1) == 0) ? 1 : -1;
                float scaleY = (((tileOrientation >> 1) & 1) == 0) ? 1 : -1;
                float rotate = (((tileOrientation >> 2) & 1) == 0) ? 0 : 90;
                return $"id: {tileId} xs: {scaleX} ys: {scaleY} r: {rotate}";
            }

            return "";
        }

        context.DrawText(new FormattedText(
            $"mouse: ({pointerPosition.X}, {pointerPosition.Y})\n" +
            $"view: ({-translation.X}, {-translation.Y}, {-translation.X + Bounds.Width}, {-translation.Y + Bounds.Height})\n" +
            $"category: {vm?.CategorySelected}\n" +
            $"render time: <{renderTimeMs} ms\n" +
            $"hovered item: {hoveredObject}\n" +
            $"hovered tile: {GetTileInfo(hoveredTile)}\n" +
            $"selected tile: {GetTileInfo(vm?.SelectedTileData)}",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, new SolidColorBrush(Colors.White)),
            new Point(0, 0));
    }

    #endregion

    #region Pointer input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Touch)
        {
            TouchPressed(e);
            e.Handled = true;
            return;
        }

        if (vm is null)
            return;

        PointerPoint pointerPoint = e.GetCurrentPoint(this);
        InteractionMode interactionMode = GetInteractionMode();

        pointerPosition = e.GetPosition(this);
        pointerPositionInRoom = (pointerPosition - translation) / scaling;
        lastKeyModifiers = e.KeyModifiers;

        Focus();

        if (pointerPoint.Properties.IsMiddleButtonPressed
            || (interactionMode == InteractionMode.Items && pointerPoint.Properties.IsRightButtonPressed))
        {
            TranslationMoveOnPressed();
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        if (interactionMode == InteractionMode.Items && pointerPoint.Properties.IsLeftButtonPressed)
        {
            e.Pointer.Capture(this);

            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                SceneItem? hit = HitTest(pointerPositionInRoom);
                if (hit is not null)
                {
                    // Alt+click on an object: duplicate it and drag the copy (WPF behavior).
                    UndertaleRoom.Layer? layer = hit.Layer ?? (vm.FindCategoryOfItem(hit.Object) as UndertaleRoom.Layer);
                    (int posX, int posY) = hit.GetModelPosition();
                    if (hit.Object is UndertaleObject hitObject && AddObjectCopy(vm.Room, layer, hitObject, new Point(posX, posY), -1) is UndertaleObject copy)
                    {
                        sceneDirty = true;
                        EnsureScene();
                        vm.RoomTreeItemsSelectedItem = copy;
                        BeginItemDrag(scene!.FindItem(copy));
                    }
                }
                else
                {
                    // Alt+drag over empty space: paint copies of the selected object on grid cells.
                    paintingTiles = true;
                    paintedCells.Clear();
                    PaintObjectsAtPointer();
                }
            }
            else
            {
                SceneItem? hit = HitTest(pointerPositionInRoom);
                if (hit is not null)
                {
                    dragLeft = dragRight = dragTop = dragBottom = false;
                    if (ReferenceEquals(hit.Object, vm.RoomTreeItemsSelectedItem))
                        TryBeginEdgeResize(hit); // resize handles only work on the already-selected object

                    vm.RoomTreeItemsSelectedItem = hit.Object;
                    BeginItemDrag(hit);
                }
                else
                {
                    dragLeft = dragRight = dragTop = dragBottom = false;
                    if (!vm.IsSelectAnyLayerEnabled)
                        vm.RoomTreeItemsSelectedItem = vm.FindItemFromCategory(vm.CategorySelected);
                    else
                        vm.RoomTreeItemsSelectedItem = null;
                }
            }

            InvalidateVisual();
            e.Handled = true;
        }
        else if (interactionMode == InteractionMode.Tiles)
        {
            UndertaleRoom.Layer? tilesLayer = GetSelectedTilesLayer();
            if (tilesLayer is not null)
            {
                e.Pointer.Capture(this);
                bool changed = false;
                if (pointerPoint.Properties.IsLeftButtonPressed)
                    changed |= SetLayerTileAtPointer(tilesLayer, vm.SelectedTileData);
                else if (pointerPoint.Properties.IsRightButtonPressed)
                    changed |= SetLayerTileAtPointer(tilesLayer, 0);
                if (changed)
                    InvalidateVisual();
                e.Handled = true;
            }
        }
        else if (interactionMode == InteractionMode.RoomTiles)
        {
            e.Pointer.Capture(this);
            bool changed = false;
            if (pointerPoint.Properties.IsLeftButtonPressed)
                changed |= SetRoomTileAtPointer(vm.SelectedTileResource, vm.SelectedTileSourceRect, overrideGrid: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            else if (pointerPoint.Properties.IsRightButtonPressed)
                changed |= RemoveRoomTileAtPointer();
            if (changed)
                InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Touch)
        {
            TouchMoved(e);
            e.Handled = true;
            return;
        }

        if (vm is null)
            return;

        PointerPoint pointerPoint = e.GetCurrentPoint(this);
        bool leftPressed = pointerPoint.Properties.IsLeftButtonPressed;
        bool rightPressed = pointerPoint.Properties.IsRightButtonPressed;

        pointerPosition = e.GetPosition(this);
        pointerPositionInRoom = (pointerPosition - translation) / scaling;
        lastKeyModifiers = e.KeyModifiers;

        bool needsRedraw = false;

        TranslationMoveOnMoved();
        if (translationHasMoved)
            needsRedraw = true;

        InteractionMode interactionMode = GetInteractionMode();

        if (interactionMode == InteractionMode.Items)
        {
            if (dragItem is not null && leftPressed)
            {
                if (dragLeft || dragRight || dragTop || dragBottom)
                    DragResize();
                else
                    DragMove(overrideGrid: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                needsRedraw = true;
            }
            else if (paintingTiles && leftPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                if (PaintObjectsAtPointer())
                    needsRedraw = true;
            }

            if (dragItem is null && !paintingTiles && !translationMoving)
            {
                SceneItem? hit = HitTest(pointerPositionInRoom);
                object? newHovered = hit?.Object;
                if (!ReferenceEquals(newHovered, hoveredObject))
                {
                    hoveredObject = newHovered;
                    needsRedraw = true;
                }
            }
        }
        else if (interactionMode == InteractionMode.Tiles)
        {
            UndertaleRoom.Layer? tilesLayer = GetSelectedTilesLayer();
            if (tilesLayer is not null)
            {
                if (leftPressed)
                    needsRedraw |= SetLayerTileAtPointer(tilesLayer, vm.SelectedTileData);
                else if (rightPressed)
                    needsRedraw |= SetLayerTileAtPointer(tilesLayer, 0);

                hoveredTile = GetLayerTileAtPointer(tilesLayer);
            }
            else
            {
                hoveredTile = null;
            }
        }
        else if (interactionMode == InteractionMode.RoomTiles)
        {
            if (leftPressed)
                needsRedraw |= SetRoomTileAtPointer(vm.SelectedTileResource, vm.SelectedTileSourceRect, overrideGrid: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            else if (rightPressed)
                needsRedraw |= RemoveRoomTileAtPointer();

            // Hovered legacy tile (used for the outline and right-click erasing).
            Tile? hoveredRoomTile = GetRoomTileAtPointer();
            if (!ReferenceEquals(hoveredRoomTile, hoveredObject))
            {
                hoveredObject = hoveredRoomTile;
                needsRedraw = true;
            }
        }

        vm.StatusText = $"({Math.Floor(pointerPositionInRoom.X)}, {Math.Floor(pointerPositionInRoom.Y)})";

        if (needsRedraw)
            InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Touch)
        {
            TouchReleased(e);
            e.Handled = true;
            return;
        }

        if (vm is null)
            return;

        InteractionMode interactionMode = GetInteractionMode();

        if (interactionMode == InteractionMode.Tiles && e.InitialPressMouseButton == MouseButton.Middle)
        {
            if (!translationHasMoved)
            {
                UndertaleRoom.Layer? tilesLayer = GetSelectedTilesLayer();
                uint? tile = tilesLayer is not null ? GetLayerTileAtPointer(tilesLayer) : null;
                if (tile is not null)
                    vm.SelectedTileData = (uint)tile;
            }
        }
        else if (interactionMode == InteractionMode.RoomTiles && e.InitialPressMouseButton == MouseButton.Middle)
        {
            if (!translationHasMoved)
            {
                UndertaleRoom.Tile? tile = GetRoomTileAtPointer();
                if (tile is not null)
                {
                    vm.SelectedTileResource = tile.ObjectDefinition;
                    vm.SelectedTileSourceRect = new(tile.SourceX, tile.SourceY, tile.Width, tile.Height);
                }
            }
        }

        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            ResetDragState();
            paintingTiles = false;
            paintedCells.Clear();
        }

        TranslationMoveOnReleased();
        e.Pointer.Capture(null);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (vm is null)
            return;

        // Same granularity as the WPF editor: 2^(1/8) per wheel step, clamped to [0.001, 1000].
        double factor = Math.Pow(2, e.Delta.Y / 8.0);
        double newScale = Math.Clamp(scaling * factor, 0.001, 1000);

        Point wheelPosition = e.GetPosition(this);
        translation = wheelPosition - (wheelPosition - translation) * (newScale / scaling);
        scaling = newScale;

        vm.Zoom = scaling;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        translationMoving = false;
        ResetDragState();
        paintingTiles = false;
        paintedCells.Clear();

        if (e.Pointer.Type == PointerType.Touch)
            ResetTouchState();
    }

    #endregion

    #region Keyboard input

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (vm is null)
            return;

        switch (e.Key)
        {
            case Key.Space:
                TranslationMoveOnPressed();
                e.Handled = true;
                break;

            case Key.F:
                FocusOnSelectedItem();
                e.Handled = true;
                break;

            case Key.Delete:
                DeleteSelected();
                e.Handled = true;
                break;

            case Key.X when vm.RoomTreeItemsSelectedItem is GameObject goX:
                FlipGameObject(goX, horizontal: true);
                e.Handled = true;
                break;

            case Key.Y when vm.RoomTreeItemsSelectedItem is GameObject goY:
                FlipGameObject(goY, horizontal: false);
                e.Handled = true;
                break;

            case Key.OemMinus:
                MoveSelectedItem(-1);
                e.Handled = true;
                break;

            case Key.OemPlus:
                MoveSelectedItem(1);
                e.Handled = true;
                break;

            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                CopySelected();
                e.Handled = true;
                break;

            case Key.V when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                Paste(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;

            case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                Undo();
                e.Handled = true;
                break;
        }

        if (e.Handled)
            InvalidateVisual();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (vm is null)
            return;

        if (e.Key == Key.Space)
        {
            TranslationMoveOnReleased();
            e.Handled = true;
        }
    }

    #endregion

    #region Panning / zooming

    void TranslationMoveOnPressed()
    {
        Focus();
        translationMoving = true;
        translationMoveOffset = pointerPosition - translation;
    }

    void TranslationMoveOnMoved()
    {
        if (translationMoving)
        {
            translationHasMoved = true;
            translation = pointerPosition - translationMoveOffset;
        }
    }

    void TranslationMoveOnReleased()
    {
        translationMoving = false;
        translationHasMoved = false;
    }

    #endregion

    #region Selection / moving

    SceneItem? HitTest(Point roomPoint)
    {
        if (vm is null)
            return null;

        List<SceneItem> items = SceneItems();
        for (int i = items.Count - 1; i >= 0; i--)
        {
            SceneItem item = items[i];
            if (!item.Valid)
                continue;
            if (!(vm.IsSelectAnyLayerEnabled || vm.CategorySelected is null || Equals(item.Category, vm.CategorySelected)))
                continue;
            if (item.HitTest(roomPoint))
                return item;
        }

        return null;
    }

    void BeginItemDrag(SceneItem? item)
    {
        dragItem = null;

        if (item?.Object is not (GameObject or Tile or SpriteInstance or ParticleSystemInstance))
            return;
        if (vm is null || vm.IsLocked)
            return;

        dragItem = item;
        (int modelX, int modelY) = item.GetModelPosition();
        dragHotpoint = pointerPositionInRoom - (new Vector(modelX, modelY) + item.LayerOffset);
        undoStack.Push(item.Object is UndertaleObject obj ? CreateSnapshot(obj) : default);
    }

    void DragMove(bool overrideGrid)
    {
        if (vm is null || dragItem is null || vm.IsLocked)
            return;

        double x = pointerPositionInRoom.X - dragHotpoint.X - dragItem.LayerOffset.X;
        double y = pointerPositionInRoom.Y - dragHotpoint.Y - dragItem.LayerOffset.Y;

        bool snap = vm.IsGridEnabled != overrideGrid;
        if (snap)
        {
            // Snap to the nearest grid multiple (WPF formula). Ctrl disables snapping.
            if (lastKeyModifiers.HasFlag(KeyModifiers.Control))
            {
                x = Math.Round(x);
                y = Math.Round(y);
            }
            else
            {
                double gridWidth = Math.Max((double)vm.GridWidth, 1);
                double gridHeight = Math.Max((double)vm.GridHeight, 1);
                if (lastKeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    gridWidth /= 2;
                    gridHeight /= 2;
                }
                x = Math.Round(x / gridWidth) * gridWidth;
                y = Math.Round(y / gridHeight) * gridHeight;
            }
        }

        dragItem.SetModelPosition((int)Math.Round(x), (int)Math.Round(y));
        dragItem.Refresh();
    }

    // Ported from the WPF editor: dragging the edges of an already-selected game object
    // resizes it by scaling around the opposite side.
    void TryBeginEdgeResize(SceneItem item)
    {
        if (vm is null || item.Object is not GameObject gameObject || gameObject.ObjectDefinition?.Sprite is null)
            return;

        Rect hitRect = item.HitRect();
        double edgeMargin = 4 / scaling;

        dragObjectOrigin = new Point(gameObject.X, gameObject.Y);
        if (Math.Abs(pointerPositionInRoom.X - hitRect.X) < edgeMargin)
        {
            dragLeft = true;
            dragObjectOrigin = dragObjectOrigin.WithX(dragObjectOrigin.X + (gameObject.ScaleX * (gameObject.ObjectDefinition.Sprite.Width + gameObject.SpriteXOffset)));
        }
        else if (Math.Abs(hitRect.Right - pointerPositionInRoom.X) < edgeMargin)
        {
            dragRight = true;
            dragObjectOrigin = dragObjectOrigin.WithX(dragObjectOrigin.X - (gameObject.ScaleX * -gameObject.SpriteXOffset));
        }

        if (Math.Abs(pointerPositionInRoom.Y - hitRect.Y) < edgeMargin)
        {
            dragTop = true;
            dragObjectOrigin = dragObjectOrigin.WithY(dragObjectOrigin.Y + (gameObject.ScaleY * (gameObject.ObjectDefinition.Sprite.Height + gameObject.SpriteYOffset)));
        }
        else if (Math.Abs(hitRect.Bottom - pointerPositionInRoom.Y) < edgeMargin)
        {
            dragBottom = true;
            dragObjectOrigin = dragObjectOrigin.WithY(dragObjectOrigin.Y + (gameObject.ScaleY * gameObject.SpriteYOffset));
        }
    }

    void DragResize()
    {
        if (vm is null || dragItem?.Object is not GameObject gameObject || gameObject.ObjectDefinition?.Sprite is null)
            return;

        double modifierValue = 1;
        if (lastKeyModifiers.HasFlag(KeyModifiers.Control))
            modifierValue = 10;
        else if (lastKeyModifiers.HasFlag(KeyModifiers.Shift))
            modifierValue = 2;

        float spriteWidth = gameObject.ObjectDefinition.Sprite.Width;
        float spriteHeight = gameObject.ObjectDefinition.Sprite.Height;
        float offsetX = -gameObject.SpriteXOffset;
        float offsetY = -gameObject.SpriteYOffset;
        Point objOrigin = dragObjectOrigin;

        if (dragLeft)
        {
            double newXScale = Math.Ceiling(((objOrigin.X - pointerPositionInRoom.X) / spriteWidth) * modifierValue) / modifierValue;
            double newXPos = objOrigin.X - (newXScale * (-offsetX + spriteWidth));
            if (newXScale != 0 && !double.IsNaN(newXScale) && !double.IsInfinity(newXScale)
                                  && !double.IsNaN(newXPos) && !double.IsInfinity(newXPos))
            {
                gameObject.ScaleX = (float)newXScale;
                gameObject.X = (int)newXPos;
            }
        }
        else if (dragRight)
        {
            double newXScale = Math.Ceiling(((pointerPositionInRoom.X - objOrigin.X) / spriteWidth) * modifierValue) / modifierValue;
            double newXPos = objOrigin.X + (newXScale * offsetX);
            if (newXScale != 0 && !double.IsNaN(newXScale) && !double.IsInfinity(newXScale)
                                  && !double.IsNaN(newXPos) && !double.IsInfinity(newXPos))
            {
                gameObject.ScaleX = (float)newXScale;
                gameObject.X = (int)newXPos;
            }
        }

        if (dragTop)
        {
            double newYScale = Math.Ceiling(((objOrigin.Y - pointerPositionInRoom.Y) / spriteHeight) * modifierValue) / modifierValue;
            double newYPos = objOrigin.Y - (newYScale * (-offsetY + spriteHeight));
            if (newYScale != 0 && !double.IsNaN(newYScale) && !double.IsInfinity(newYScale)
                                  && !double.IsNaN(newYPos) && !double.IsInfinity(newYPos))
            {
                gameObject.ScaleY = (float)newYScale;
                gameObject.Y = (int)newYPos;
            }
        }
        else if (dragBottom)
        {
            double newYScale = Math.Ceiling(((pointerPositionInRoom.Y - objOrigin.Y) / spriteHeight) * modifierValue) / modifierValue;
            double newYPos = objOrigin.Y + (newYScale * offsetY);
            if (newYScale != 0 && !double.IsNaN(newYScale) && !double.IsInfinity(newYScale)
                                  && !double.IsNaN(newYPos) && !double.IsInfinity(newYPos))
            {
                gameObject.ScaleY = (float)newYScale;
                gameObject.Y = (int)newYPos;
            }
        }

        dragItem.Refresh();
    }

    void ResetDragState()
    {
        dragItem = null;
        dragLeft = dragRight = dragTop = dragBottom = false;
    }

    void FocusOnSelectedItem()
    {
        if (vm is null)
            return;

        SceneItem? item = scene?.FindItem(vm.RoomTreeItemsSelectedItem);
        if (item is not null && item.Valid)
        {
            translation = new(-item.Bounds.X * scaling + (Bounds.Width / 2), -item.Bounds.Y * scaling + (Bounds.Height / 2));
            InvalidateVisual();
        }
    }

    #endregion

    #region Tile layer painting (GMS2 tile layers)

    bool GetLayerTileIndexesAtPointer(UndertaleRoom.Layer tilesLayer, out (int x, int y) point)
    {
        point = default;

        if (tilesLayer.TilesData.Background is null)
            return false;

        int x = (int)Math.Floor((pointerPositionInRoom.X - tilesLayer.XOffset) / tilesLayer.TilesData.Background.GMS2TileWidth);
        int y = (int)Math.Floor((pointerPositionInRoom.Y - tilesLayer.YOffset) / tilesLayer.TilesData.Background.GMS2TileHeight);

        if (y >= 0 && x >= 0
            && y < tilesLayer.TilesData.TileData.Length
            && x < tilesLayer.TilesData.TileData[y].Length)
        {
            point = (x, y);
            return true;
        }

        return false;
    }

    uint? GetLayerTileAtPointer(UndertaleRoom.Layer tilesLayer)
    {
        if (GetLayerTileIndexesAtPointer(tilesLayer, out (int x, int y) point))
            return tilesLayer.TilesData.TileData[point.y][point.x];

        return null;
    }

    bool SetLayerTileAtPointer(UndertaleRoom.Layer tilesLayer, uint tileData)
    {
        if (vm is null || vm.IsLocked)
            return false;

        if (GetLayerTileIndexesAtPointer(tilesLayer, out (int x, int y) point))
        {
            if ((tileData & UndertaleRoomViewModel.TILE_ID) < tilesLayer.TilesData.Background.GMS2TileCount
                && tilesLayer.TilesData.TileData[point.y][point.x] != tileData)
            {
                tilesLayer.TilesData.TileData[point.y][point.x] = tileData;
                return true;
            }
        }

        return false;
    }

    #endregion

    #region Legacy tile painting (GMS1 room tiles / GMS2 asset layer tiles)

    UndertaleRoom.Layer? GetSelectedTilesLayer()
    {
        if (vm!.RoomTreeItemsSelectedItem is UndertaleRoom.Layer { LayerType: UndertaleRoom.LayerType.Tiles } tilesLayer)
            return tilesLayer;
        return null;
    }

    UndertaleRoom.Layer? GetSelectedLegacyTilesLayer()
    {
        if (vm!.RoomTreeItemsSelectedItem is UndertalePointerList<Tile> && vm.CategorySelected is UndertaleRoom.Layer { LayerType: UndertaleRoom.LayerType.Assets } layer)
            return layer;
        return null;
    }

    bool IsRoomTilesSelected()
    {
        return vm!.PropertiesContent is UndertaleRoomViewModel.TilesViewModel;
    }

    InteractionMode GetInteractionMode()
    {
        if (GetSelectedTilesLayer() is not null)
            return InteractionMode.Tiles;
        if (IsRoomTilesSelected())
            return InteractionMode.RoomTiles;
        return InteractionMode.Items;
    }

    // Tiles of the list that is currently being painted into ("Tiles" for GMS1 room tiles).
    bool IsPaintTargetTile(SceneItem item, UndertaleRoom.Layer? legacyTilesLayer)
    {
        object targetCategory = (object?)legacyTilesLayer ?? "Tiles";
        return Equals(item.Category, targetCategory) && item.Object is Tile;
    }

    Tile? GetRoomTileAtPointer()
    {
        if (vm is null)
            return null;

        UndertaleRoom.Layer? legacyTilesLayer = GetSelectedLegacyTilesLayer();

        List<SceneItem> items = SceneItems();
        for (int i = items.Count - 1; i >= 0; i--)
        {
            SceneItem item = items[i];
            if (!item.Valid)
                continue;
            if (!IsPaintTargetTile(item, legacyTilesLayer))
                continue;
            if (item.HitTest(pointerPositionInRoom) && item.Object is Tile tile)
                return tile;
        }

        return null;
    }

    Tile? GetRoomTileAtExactPosition(double x, double y)
    {
        if (vm is null)
            return null;

        UndertaleRoom.Layer? legacyTilesLayer = GetSelectedLegacyTilesLayer();

        foreach (SceneItem item in SceneItems())
        {
            if (!item.Valid || !IsPaintTargetTile(item, legacyTilesLayer))
                continue;
            if (item.Object is Tile tile && tile.X == (int)x && tile.Y == (int)y)
                return tile;
        }

        return null;
    }

    bool SetRoomTileAtPointer(UndertaleNamedResource? resource, Rect? sourceRect, bool overrideGrid)
    {
        if (vm is null || vm.IsLocked)
            return false;

        if (resource is null || sourceRect is not Rect sourceRectNN)
            return false;

        double x = pointerPositionInRoom.X;
        double y = pointerPositionInRoom.Y;
        if (vm.IsGridEnabled != overrideGrid)
        {
            x = Math.Floor(x / Math.Max((double)vm.GridWidth, 1)) * vm.GridWidth;
            y = Math.Floor(y / Math.Max((double)vm.GridHeight, 1)) * vm.GridHeight;
        }

        Tile? tile = GetRoomTileAtExactPosition(x, y);
        if (tile is null)
        {
            UndertaleRoom.Layer? legacyTilesLayer = GetSelectedLegacyTilesLayer();
            tile = legacyTilesLayer is not null ? vm.AddLegacyTileInstance(legacyTilesLayer) : vm.AddTile();
            sceneDirty = true;
        }

        tile.ObjectDefinition = resource;
        tile.SourceX = (int)sourceRectNN.X;
        tile.SourceY = (int)sourceRectNN.Y;
        tile.Width = (uint)sourceRectNN.Width;
        tile.Height = (uint)sourceRectNN.Height;
        tile.X = (int)x;
        tile.Y = (int)y;

        scene?.FindItem(tile)?.Refresh();
        return true;
    }

    bool RemoveRoomTileAtPointer()
    {
        if (vm is null || vm.IsLocked)
            return false;

        Tile? tile = GetRoomTileAtPointer();
        if (tile is null)
            return false;

        UndertaleRoom.Layer? legacyTilesLayer = GetSelectedLegacyTilesLayer();
        if (legacyTilesLayer is not null)
        {
            vm.RemoveAsset(legacyTilesLayer.AssetsData.LegacyTiles, tile);
        }
        else
        {
            vm.RemoveTile(tile);
        }
        hoveredObject = null;
        sceneDirty = true;
        return true;
    }

    #endregion

    #region Alt+drag painting (copies of the selected object on grid cells)

    bool PaintObjectsAtPointer()
    {
        if (vm is null || vm.IsLocked)
            return false;

        UndertaleRoom room = vm.Room;
        UndertaleObject? other = vm.RoomTreeItemsSelectedItem as UndertaleObject;
        if (other is null)
            return false;

        // WPF "GetGridMouseCoordinates": Ctrl halves the paint grid, Shift doubles it.
        double gridWidth = Math.Max((double)vm.GridWidth, 1);
        double gridHeight = Math.Max((double)vm.GridHeight, 1);
        if (lastKeyModifiers.HasFlag(KeyModifiers.Control))
        {
            gridWidth = Math.Max(gridWidth / 2, 1);
            gridHeight = Math.Max(gridHeight / 2, 1);
        }
        else if (lastKeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            gridWidth *= 2;
            gridHeight *= 2;
        }

        (int cellX, int cellY) cell = ((int)Math.Floor(pointerPositionInRoom.X / gridWidth),
                                       (int)Math.Floor(pointerPositionInRoom.Y / gridHeight));
        if (!paintedCells.Add(cell))
            return false;

        Point pos = new(cell.cellX * gridWidth, cell.cellY * gridHeight);

        UndertaleRoom.Layer? layer = (scene?.FindItem(other)?.Layer) ?? (vm.FindCategoryOfItem(other) as UndertaleRoom.Layer);
        if (layer is not null && layer.AssetsData is null && layer.InstancesData is null)
            return false;

        if (AddObjectCopy(room, layer, other, pos, -1) is UndertaleObject copy)
        {
            sceneDirty = true;
            vm.RoomTreeItemsSelectedItem = copy; // WPF selects each painted copy
            return true;
        }

        return false;
    }

    #endregion

    #region Copy / paste / duplicate / delete / undo

    void CopySelected()
    {
        if (vm is null)
            return;
        if (vm.RoomTreeItemsSelectedItem is UndertaleObject obj)
            vm.MainVM.InternalClipboard = obj;
    }

    void Paste(bool shiftPressed)
    {
        if (vm is null || vm.MainVM.InternalClipboard is not UndertaleObject copied)
            return;

        UndertaleRoom room = vm.Room;
        UndertaleRoom.Layer? layer = ResolvePasteLayer(copied);

        Point pos = IsPointerOver ? pointerPositionInRoom : default;
        if (!shiftPressed)
        {
            pos = new Point(
                Math.Floor(pos.X / Math.Max((double)vm.GridWidth, 1)) * vm.GridWidth,
                Math.Floor(pos.Y / Math.Max((double)vm.GridHeight, 1)) * vm.GridHeight);
        }

        if (AddObjectCopy(room, layer, copied, pos, -1) is UndertaleObject newObj)
        {
            sceneDirty = true;
            vm.RoomTreeItemsSelectedItem = newObj;
            InvalidateVisual();
        }
    }

    // Layer the clipboard content is pasted into (WPF resolves it from the current selection).
    UndertaleRoom.Layer? ResolvePasteLayer(UndertaleObject copied)
    {
        if (vm is null)
            return null;

        UndertaleRoom.Layer? layer = vm.FindCategoryOfItem(vm.RoomTreeItemsSelectedItem) as UndertaleRoom.Layer;
        if (layer is not null)
            return layer;

        bool gms2 = vm.Room.Flags.HasFlag(RoomEntryFlags.IsGMS2) || vm.Room.Flags.HasFlag(RoomEntryFlags.IsGM2024_13);
        if (!gms2)
            return null;

        // Fall back to the first suitable layer, mirroring the drag & drop behavior.
        return copied switch
        {
            GameObject => vm.Room.Layers.FirstOrDefault(l => l.LayerType == LayerType.Instances),
            Tile or SpriteInstance or ParticleSystemInstance => vm.Room.Layers.FirstOrDefault(l => l.LayerType == LayerType.Assets),
            _ => null,
        };
    }

    // Port of the WPF "AddObjectCopy".
    UndertaleObject? AddObjectCopy(UndertaleRoom room, UndertaleRoom.Layer? layer, UndertaleObject obj, Point pos, int insertIndex)
    {
        if (vm is null || room is null || obj is null)
            return null;

        UndertaleData data = vm.MainVM.Data!;
        int IndexOf(int currentIndex, int count) => insertIndex > -1 ? insertIndex : count;

        switch (obj)
        {
            case Tile tile:
            {
                if (layer is not null && layer.AssetsData is null)
                    return null;

                Tile newTile = new()
                {
                    X = (int)pos.X,
                    Y = (int)pos.Y,
                    spriteMode = tile.spriteMode,
                    ObjectDefinition = tile.ObjectDefinition,
                    SpriteDefinition = tile.SpriteDefinition,
                    SourceX = tile.SourceX,
                    SourceY = tile.SourceY,
                    Width = tile.Width,
                    Height = tile.Height,
                    TileDepth = tile.TileDepth,
                    InstanceID = data.GeneralInfo.LastTile++,
                    ScaleX = tile.ScaleX,
                    ScaleY = tile.ScaleY,
                    Color = tile.Color
                };

                if (layer is not null)
                {
                    layer.AssetsData.LegacyTiles ??= new UndertalePointerList<Tile>();
                    layer.AssetsData.LegacyTiles.Insert(IndexOf(-1, layer.AssetsData.LegacyTiles.Count), newTile);
                }
                else
                {
                    room.Tiles.Insert(IndexOf(-1, room.Tiles.Count), newTile);
                }

                return newTile;
            }
            case GameObject gameObj:
            {
                if (layer is not null && layer.InstancesData is null)
                    return null;

                GameObject newGameObj = new()
                {
                    X = (int)pos.X,
                    Y = (int)pos.Y,
                    ObjectDefinition = gameObj.ObjectDefinition,
                    InstanceID = data.GeneralInfo.LastObj++,
                    CreationCode = gameObj.CreationCode,
                    ScaleX = gameObj.ScaleX,
                    ScaleY = gameObj.ScaleY,
                    Color = gameObj.Color,
                    Rotation = gameObj.Rotation,
                    PreCreateCode = gameObj.PreCreateCode,
                    ImageSpeed = gameObj.ImageSpeed,
                    ImageIndex = gameObj.ImageIndex
                };

                room.GameObjects.Insert(IndexOf(-1, room.GameObjects.Count), newGameObj);
                if (layer is not null)
                    layer.InstancesData.Instances.Insert(IndexOf(-1, layer.InstancesData.Instances.Count), newGameObj);

                return newGameObj;
            }
            case SpriteInstance sprInst:
            {
                if (layer is null || layer.AssetsData is null)
                    return null;

                SpriteInstance newSprInst = new()
                {
                    Name = SpriteInstance.GenerateRandomName(data),
                    Sprite = sprInst.Sprite,
                    X = (int)pos.X,
                    Y = (int)pos.Y,
                    ScaleX = sprInst.ScaleX,
                    ScaleY = sprInst.ScaleY,
                    Color = sprInst.Color,
                    AnimationSpeed = sprInst.AnimationSpeed,
                    AnimationSpeedType = sprInst.AnimationSpeedType,
                    FrameIndex = sprInst.FrameIndex,
                    Rotation = sprInst.Rotation
                };

                layer.AssetsData.Sprites ??= new UndertalePointerList<SpriteInstance>();
                layer.AssetsData.Sprites.Insert(IndexOf(-1, layer.AssetsData.Sprites.Count), newSprInst);

                return newSprInst;
            }
            case ParticleSystemInstance partSysInst:
            {
                if (layer is null || layer.AssetsData is null)
                    return null;

                ParticleSystemInstance newPartSysInst = new()
                {
                    Name = ParticleSystemInstance.GenerateRandomName(data),
                    InstanceID = ++data.LastParticleSystemInstanceID,
                    ParticleSystem = partSysInst.ParticleSystem,
                    X = (int)pos.X,
                    Y = (int)pos.Y,
                    ScaleX = partSysInst.ScaleX,
                    ScaleY = partSysInst.ScaleY,
                    Color = partSysInst.Color,
                    Rotation = partSysInst.Rotation
                };

                layer.AssetsData.ParticleSystems ??= new UndertalePointerList<ParticleSystemInstance>();
                layer.AssetsData.ParticleSystems.Insert(IndexOf(-1, layer.AssetsData.ParticleSystems.Count), newPartSysInst);

                return newPartSysInst;
            }
            default:
                return null;
        }
    }

    void DeleteSelected()
    {
        if (vm is null)
            return;

        UndertaleRoom room = vm.Room;
        bool gms2 = room.Flags.HasFlag(RoomEntryFlags.IsGMS2) || room.Flags.HasFlag(RoomEntryFlags.IsGM2024_13);

        switch (vm.RoomTreeItemsSelectedItem)
        {
            case UndertaleRoom.Background bg:
                bg.Enabled = false;
                bg.BackgroundDefinition = null;
                break;

            case View view:
                view.Enabled = false;
                break;

            case Tile tile:
                if (gms2)
                {
                    foreach (UndertaleRoom.Layer layer in room.Layers)
                        if (layer.AssetsData?.LegacyTiles is not null)
                            layer.AssetsData.LegacyTiles.Remove(tile);
                }
                vm.RemoveTile(tile);
                break;

            case GameObject gameObj:
                if (gms2)
                {
                    foreach (UndertaleRoom.Layer layer in room.Layers)
                        if (layer.InstancesData is not null)
                            layer.InstancesData.Instances.Remove(gameObj);
                }
                room.GameObjects.Remove(gameObj);
                break;

            case SpriteInstance sprInst:
                foreach (UndertaleRoom.Layer layer in room.Layers)
                    if (layer.AssetsData?.Sprites is not null)
                        layer.AssetsData.Sprites.Remove(sprInst);
                break;

            case ParticleSystemInstance partSysInst:
                foreach (UndertaleRoom.Layer layer in room.Layers)
                    if (layer.AssetsData?.ParticleSystems is not null)
                        layer.AssetsData.ParticleSystems.Remove(partSysInst);
                break;

            case UndertaleRoom.Layer layer:
                vm.RemoveLayer(layer);
                break;

            default:
                return;
        }

        sceneDirty = true;
        InvalidateVisual();
    }

    void MoveSelectedItem(int dist)
    {
        if (vm is null)
            return;

        object sel = vm.RoomTreeItemsSelectedItem;
        UndertaleRoom.Layer? layer = scene?.FindItem(sel)?.Layer ?? (vm.FindCategoryOfItem(sel) as UndertaleRoom.Layer);

        IList? list = sel switch
        {
            GameObject => layer?.InstancesData is not null ? layer.InstancesData.Instances : (IList)vm.Room.GameObjects,
            Tile => layer?.AssetsData?.LegacyTiles is not null ? layer.AssetsData.LegacyTiles : (IList)vm.Room.Tiles,
            SpriteInstance => layer?.AssetsData?.Sprites is not null ? layer.AssetsData.Sprites : null,
            ParticleSystemInstance => layer?.AssetsData?.ParticleSystems is not null ? layer.AssetsData.ParticleSystems : null,
            _ => null,
        };
        if (list is null)
            return;

        int index = list.IndexOf(sel);
        int newIndex = Math.Clamp(index + dist, 0, list.Count - 1);
        if (index < 0 || newIndex == index)
            return;

        object prevObj = list[newIndex]!;
        list[newIndex] = sel;
        list[index] = prevObj;

        sceneDirty = true;
        InvalidateVisual();
    }

    void FlipGameObject(GameObject gameObject, bool horizontal)
    {
        if (gameObject.ObjectDefinition?.Sprite is null)
            return;

        // WPF formulas.
        if (horizontal)
        {
            gameObject.ScaleX *= -1;
            gameObject.X -= (((int)gameObject.ObjectDefinition.Sprite.Width - gameObject.ObjectDefinition.Sprite.OriginX) * (int)gameObject.ScaleX);
        }
        else
        {
            gameObject.ScaleY *= -1;
            gameObject.Y -= (((int)gameObject.ObjectDefinition.Sprite.Height - gameObject.ObjectDefinition.Sprite.OriginY) * (int)gameObject.ScaleY);
        }

        scene?.FindItem(gameObject)?.Refresh();
        InvalidateVisual();
    }

    void Undo()
    {
        if (vm is null || undoStack.Count == 0)
            return;

        // WPF only restores while the object is still selected.
        while (undoStack.TryPop(out MoveSnapshot snapshot))
        {
            if (!ReferenceEquals(snapshot.Object, vm.RoomTreeItemsSelectedItem))
                continue;

            switch (snapshot.Object)
            {
                case GameObject gameObj:
                    gameObj.X = snapshot.X;
                    gameObj.Y = snapshot.Y;
                    gameObj.ScaleX = snapshot.ScaleX;
                    gameObj.ScaleY = snapshot.ScaleY;
                    break;
                case Tile tile:
                    tile.X = snapshot.X;
                    tile.Y = snapshot.Y;
                    break;
                case SpriteInstance spr:
                    spr.X = snapshot.X;
                    spr.Y = snapshot.Y;
                    break;
                case ParticleSystemInstance partSys:
                    partSys.X = snapshot.X;
                    partSys.Y = snapshot.Y;
                    break;
            }

            scene?.FindItem(snapshot.Object)?.Refresh();
            break;
        }

        InvalidateVisual();
    }

    static MoveSnapshot CreateSnapshot(UndertaleObject obj) => obj switch
    {
        GameObject gameObj => new MoveSnapshot(gameObj, gameObj.X, gameObj.Y, gameObj.ScaleX, gameObj.ScaleY),
        Tile tile => new MoveSnapshot(tile, tile.X, tile.Y, tile.ScaleX, tile.ScaleY),
        SpriteInstance spr => new MoveSnapshot(spr, spr.X, spr.Y, spr.ScaleX, spr.ScaleY),
        ParticleSystemInstance partSys => new MoveSnapshot(partSys, partSys.X, partSys.Y, partSys.ScaleX, partSys.ScaleY),
        _ => default,
    };

    #endregion

    #region Touch

    static double Distance(Point a, Point b)
    {
        Vector v = b - a;
        return v.Length;
    }

    void StartLongPressTimer()
    {
        StopLongPressTimer();
        longPressTimer = new DispatcherTimer(TouchLongPressDuration, DispatcherPriority.Background, (_, _) => OnLongPressTimerTick());
        longPressTimer.Start();
    }

    void StopLongPressTimer()
    {
        longPressTimer?.Stop();
        longPressTimer = null;
    }

    void ResetTouchState()
    {
        StopLongPressTimer();
        touchPoints.Clear();
        touchPrimaryId = null;
        touchSecondaryId = null;
        touchTwoFingerCancelled = true;
        touchMode = TouchMode.None;
        TranslationMoveOnReleased();
    }

    void TouchPressed(PointerPressedEventArgs e)
    {
        long id = e.Pointer.Id;
        Point pos = e.GetPosition(this);
        e.Pointer.Capture(this);

        touchPoints[id] = pos;

        if (touchPrimaryId is null)
        {
            touchPrimaryId = id;
            touchStartPosition = pos;
            pointerPosition = pos;
            pointerPositionInRoom = (pointerPosition - translation) / scaling;
            touchMode = TouchMode.PossibleTap;
            StartLongPressTimer();
        }
        else if (touchSecondaryId is null)
        {
            touchSecondaryId = id;
            touchSecondStartPosition = pos;
            touchTwoFingerStartDistance = Distance(pos, touchPoints[touchPrimaryId.Value]);
            touchTwoFingerStartTime = DateTime.UtcNow;
            touchTwoFingerCancelled = false;
            StopLongPressTimer();

            // If the first finger was already dragging (pan/move), a two-finger tap is no longer
            // possible — start pinching right away.
            if (touchMode is TouchMode.Panning or TouchMode.MovingItem)
            {
                BeginPinch();
            }
        }
        else
        {
            // A third finger only complicates things; cancel any pending two-finger tap.
            touchTwoFingerCancelled = true;
        }
    }

    void TouchMoved(PointerEventArgs e)
    {
        if (vm is null)
            return;

        long id = e.Pointer.Id;
        Point pos = e.GetPosition(this);
        pointerPosition = pos;

        if (touchPoints.ContainsKey(id))
            touchPoints[id] = pos;

        if (touchMode == TouchMode.Pinching)
        {
            UpdatePinch();
            return;
        }

        // Both fingers down, gesture still undetermined: promote to a pinch once the fingers
        // clearly move apart/together (this also cancels the pending two-finger tap).
        if (touchMode == TouchMode.PossibleTap && touchSecondaryId is not null && !touchTwoFingerCancelled
            && touchPrimaryId is long primaryId)
        {
            Point p1 = touchPoints[primaryId];
            Point p2 = touchPoints[touchSecondaryId.Value];
            double dist = Distance(p1, p2);
            bool firstMoved = Distance(p1, touchStartPosition) > TouchMoveThreshold;
            bool secondMoved = Distance(p2, touchSecondStartPosition) > TouchMoveThreshold;

            if (Math.Abs(dist - touchTwoFingerStartDistance) > TouchPinchStartDistanceThreshold || firstMoved || secondMoved)
            {
                touchTwoFingerCancelled = true;
                BeginPinch();
            }
            return;
        }

        if (id != touchPrimaryId)
            return;

        pointerPositionInRoom = (pointerPosition - translation) / scaling;

        switch (touchMode)
        {
            case TouchMode.PossibleTap:
                if (Distance(pos, touchStartPosition) > TouchMoveThreshold)
                {
                    StopLongPressTimer();
                    touchMode = TouchMode.Panning;
                    TranslationMoveOnPressed();
                }
                break;
            case TouchMode.Panning:
                TranslationMoveOnMoved();
                InvalidateVisual();
                break;
            case TouchMode.MovingItem:
                TouchMoveAction();
                break;
        }

        vm.StatusText = $"({Math.Floor(pointerPositionInRoom.X)}, {Math.Floor(pointerPositionInRoom.Y)})";
    }

    void TouchReleased(PointerReleasedEventArgs e)
    {
        long id = e.Pointer.Id;
        Point pos = e.GetPosition(this);
        pointerPosition = pos;

        if (touchMode == TouchMode.Pinching)
        {
            touchPoints.Remove(id);
            if (id == touchPrimaryId) touchPrimaryId = null;
            if (id == touchSecondaryId) touchSecondaryId = null;

            if (touchPoints.Count < 2)
            {
                if (touchPoints.Count == 1)
                {
                    var remaining = touchPoints.First();
                    touchPrimaryId = remaining.Key;
                    touchMode = TouchMode.Panning;
                    touchStartPosition = remaining.Value;
                    pointerPosition = remaining.Value;
                    pointerPositionInRoom = (pointerPosition - translation) / scaling;
                    TranslationMoveOnPressed();
                }
                else
                {
                    touchMode = TouchMode.None;
                }
            }
            return;
        }

        if (id != touchPrimaryId && id != touchSecondaryId)
        {
            touchPoints.Remove(id);
            return;
        }

        // Two-finger tap: both fingers were down and the gesture never became a pinch. Picking a
        // tile under the fingers mirrors the desktop middle-click.
        if (touchMode == TouchMode.PossibleTap && touchSecondaryId is not null && !touchTwoFingerCancelled)
        {
            bool wasSecondary = id == touchSecondaryId;

            touchPoints.Remove(id);
            if (wasSecondary) touchSecondaryId = null;
            else touchPrimaryId = null;

            if (touchPoints.Count >= 2)
            {
                // A third finger remains; keep tracking a fresh pair.
                touchTwoFingerCancelled = true;
                var remaining = touchPoints.First();
                touchPrimaryId = remaining.Key;
                touchSecondaryId = null;
                touchStartPosition = remaining.Value;
                return;
            }

            // Clean two-finger tap: exactly one finger remains.
            var last = touchPoints.First();
            touchPrimaryId = last.Key;
            touchSecondaryId = null;
            touchStartPosition = last.Value;
            pointerPosition = last.Value;
            pointerPositionInRoom = (pointerPosition - translation) / scaling;

            if (DateTime.UtcNow - touchTwoFingerStartTime <= TouchTwoFingerTapDuration)
            {
                TwoFingerTapAction();

                // The remaining finger's release must not trigger a normal tap action as well,
                // so idle it until it lifts or the next press starts fresh.
                touchMode = TouchMode.None;
                touchTwoFingerCancelled = true;
            }
            else
            {
                touchMode = TouchMode.PossibleTap;
                StartLongPressTimer();
            }
            return;
        }

        if (id != touchPrimaryId)
        {
            touchPoints.Remove(id);
            return;
        }

        if (touchMode == TouchMode.PossibleTap)
        {
            StopLongPressTimer();
            pointerPosition = pos;
            pointerPositionInRoom = (pointerPosition - translation) / scaling;
            TouchPressAction();
        }
        else if (touchMode == TouchMode.Panning)
        {
            TranslationMoveOnReleased();
        }

        touchPoints.Remove(id);
        touchPrimaryId = null;
        touchSecondaryId = null;

        if (touchPoints.Count > 0)
        {
            var remaining = touchPoints.First();
            touchPrimaryId = remaining.Key;
            touchStartPosition = remaining.Value;
            pointerPosition = remaining.Value;
            pointerPositionInRoom = (pointerPosition - translation) / scaling;
            touchMode = TouchMode.Panning;
            TranslationMoveOnPressed();
        }
        else
        {
            touchMode = TouchMode.None;
        }

        InvalidateVisual();
    }

    void OnLongPressTimerTick()
    {
        if (touchMode != TouchMode.PossibleTap)
            return;
        if (touchPoints.Count != 1)
            return;

        touchMode = TouchMode.MovingItem;
        pointerPosition = touchStartPosition;
        pointerPositionInRoom = (pointerPosition - translation) / scaling;
        TouchPressAction();
        PlatformHaptics.OnLongPress();
    }

    /// <summary>
    /// Touch equivalent of the desktop middle-click: picks the tile under the fingers (GMS2 tile
    /// layers and legacy room tiles).
    /// </summary>
    void TwoFingerTapAction()
    {
        if (vm is null)
            return;

        InteractionMode interactionMode = GetInteractionMode();

        switch (interactionMode)
        {
            case InteractionMode.Tiles:
            {
                UndertaleRoom.Layer? tilesLayer = GetSelectedTilesLayer();
                if (tilesLayer is not null)
                {
                    uint? tile = GetLayerTileAtPointer(tilesLayer);
                    if (tile is not null)
                    {
                        vm.SelectedTileData = (uint)tile;
                        PlatformHaptics.OnTap();
                    }
                }
                break;
            }
            case InteractionMode.RoomTiles:
            {
                UndertaleRoom.Tile? tile = GetRoomTileAtPointer();
                if (tile is not null)
                {
                    vm.SelectedTileResource = tile.ObjectDefinition;
                    vm.SelectedTileSourceRect = new(tile.SourceX, tile.SourceY, tile.Width, tile.Height);
                    PlatformHaptics.OnTap();
                }
                break;
            }
        }
    }

    void BeginPinch()
    {
        if (touchPoints.Count < 2)
            return;

        var points = touchPoints.Values.Take(2).ToArray();
        pinchStartDistance = Distance(points[0], points[1]);
        if (pinchStartDistance <= 0)
            pinchStartDistance = 1;
        pinchStartScale = scaling;

        Point mid = new((points[0].X + points[1].X) / 2, (points[0].Y + points[1].Y) / 2);
        pinchStartRoomPoint = (mid - translation) / scaling;

        touchMode = TouchMode.Pinching;
        TranslationMoveOnReleased();
    }

    void UpdatePinch()
    {
        if (touchPoints.Count < 2 || vm is null)
            return;

        var points = touchPoints.Values.Take(2).ToArray();
        double newDist = Distance(points[0], points[1]);
        if (newDist <= 0)
            return;

        double factor = newDist / pinchStartDistance;
        double newScale = Math.Clamp(pinchStartScale * factor, 0.001, 1000);

        Point mid = new((points[0].X + points[1].X) / 2, (points[0].Y + points[1].Y) / 2);

        translation = mid - pinchStartRoomPoint * newScale;
        scaling = newScale;
        vm.Zoom = scaling;
        InvalidateVisual();
    }

    void TouchPressAction()
    {
        if (vm is null)
            return;

        EnsureScene();
        InteractionMode interactionMode = GetInteractionMode();

        switch (interactionMode)
        {
            case InteractionMode.Items:
            {
                SceneItem? hit = HitTest(pointerPositionInRoom);
                hoveredObject = hit?.Object;
                if (hit is not null)
                {
                    vm.RoomTreeItemsSelectedItem = hit.Object;
                    BeginItemDrag(hit);
                }
                else if (!vm.IsSelectAnyLayerEnabled)
                {
                    vm.RoomTreeItemsSelectedItem = vm.FindItemFromCategory(vm.CategorySelected);
                }
                else
                {
                    vm.RoomTreeItemsSelectedItem = null;
                }
                InvalidateVisual();
                break;
            }
            case InteractionMode.Tiles:
            {
                UndertaleRoom.Layer? tilesLayer = GetSelectedTilesLayer();
                if (tilesLayer is not null && !vm.IsLocked && SetLayerTileAtPointer(tilesLayer, vm.SelectedTileData))
                    InvalidateVisual();
                break;
            }
            case InteractionMode.RoomTiles:
            {
                if (!vm.IsLocked && SetRoomTileAtPointer(vm.SelectedTileResource, vm.SelectedTileSourceRect, overrideGrid: false))
                    InvalidateVisual();
                break;
            }
        }
    }

    void TouchMoveAction()
    {
        if (vm is null || vm.IsLocked)
            return;

        InteractionMode interactionMode = GetInteractionMode();

        switch (interactionMode)
        {
            case InteractionMode.Items:
                DragMove(overrideGrid: false);
                InvalidateVisual();
                break;
            case InteractionMode.Tiles:
            {
                UndertaleRoom.Layer? tilesLayer = GetSelectedTilesLayer();
                if (tilesLayer is not null && SetLayerTileAtPointer(tilesLayer, vm.SelectedTileData))
                    InvalidateVisual();
                break;
            }
            case InteractionMode.RoomTiles:
                if (SetRoomTileAtPointer(vm.SelectedTileResource, vm.SelectedTileSourceRect, overrideGrid: false))
                    InvalidateVisual();
                break;
        }
    }

    #endregion

    /// <summary>
    /// Drag &amp; drop from the data explorer onto the room canvas.
    /// </summary>
    public class UndertaleReferenceDropHandler : DropHandlerBase
    {
        public override bool Validate(object? sender, DragEventArgs e, object? sourceContext, object? targetContext, object? state)
        {
            if (sender is UndertaleRoomEditor editor
                && editor.vm is UndertaleRoomViewModel vm
                && sourceContext is DataExplorerViewModel.Item item
                && item.Value is UndertaleResource resource)
            {
                if (resource is UndertaleGameObject gameObject)
                {
                    return vm.CategorySelected is "GameObjects"
                        || vm.CategorySelected is UndertaleRoom.Layer layer && layer.LayerType == UndertaleRoom.LayerType.Instances;
                }
                else if (resource is UndertaleSprite sprite)
                {
                    return vm.CategorySelected is UndertaleRoom.Layer layer && layer.LayerType == UndertaleRoom.LayerType.Assets;
                }
            }
            return false;
        }

        public override bool Execute(object? sender, DragEventArgs e, object? sourceContext, object? targetContext, object? state)
        {
            if (sender is UndertaleRoomEditor editor
                && editor.vm is UndertaleRoomViewModel vm
                && sourceContext is DataExplorerViewModel.Item item
                && item.Value is UndertaleResource resource)
            {
                Point pointerPosition = e.GetPosition(editor);
                Point pointerPositionInRoom = (pointerPosition - editor.translation) / editor.scaling;
                int x = (int)pointerPositionInRoom.X;
                int y = (int)pointerPositionInRoom.Y;

                if (resource is UndertaleGameObject gameObject)
                {
                    if (vm.CategorySelected is "GameObjects")
                    {
                        vm.AddGameObjectInstance(layer: null, gameObject, x, y);
                    }
                    else if (vm.CategorySelected is UndertaleRoom.Layer layer && layer.LayerType == UndertaleRoom.LayerType.Instances)
                    {
                        vm.AddGameObjectInstance(layer, gameObject: gameObject, x, y);
                    }
                    else
                    {
                        return false;
                    }

                    editor.sceneDirty = true;
                    editor.InvalidateVisual();
                    return true;
                }
                else if (resource is UndertaleSprite sprite)
                {
                    if (vm.CategorySelected is UndertaleRoom.Layer layer && layer.LayerType == UndertaleRoom.LayerType.Assets)
                    {
                        vm.AddSpriteInstance(layer, sprite, x, y);
                        editor.sceneDirty = true;
                        editor.InvalidateVisual();
                        return true;
                    }
                }
            }
            return false;
        }
    }
}

/// <summary>
/// A retained draw list for a room. Items are in bottom-to-top draw order and replicate the
/// draw order and geometry of the WPF room editor.
/// </summary>
internal sealed class RoomScene
{
    public List<SceneItem> Items { get; } = new();
    public Dictionary<object, SceneItem> ItemMap { get; } = new();
    public Color BackgroundColor;
    public IBrush BackgroundBrush = Brushes.Black;

    public SceneItem? FindItem(object? obj)
        => obj is not null && ItemMap.TryGetValue(obj, out SceneItem? item) ? item : null;

    public static RoomScene Build(UndertaleRoomViewModel vm, out bool pendingImages)
    {
        RoomScene result = new();
        pendingImages = false;
        bool pending = false;

        UndertaleRoom room = vm.Room;
        ImageCache cache = vm.MainVM.ImageCache;
        bool gms2 = room.Flags.HasFlag(RoomEntryFlags.IsGMS2) || room.Flags.HasFlag(RoomEntryFlags.IsGM2024_13);

        // Room background color: WPF binds the canvas brush to "BackgroundColor" (GMS1) or
        // "BGColorLayer.BackgroundData.Color" (GMS2), defaulting to black when there is none.
        uint bgColor = gms2
            ? (room.BGColorLayer?.BackgroundData?.Color ?? 0xFF000000)
            : room.BackgroundColor;
        result.BackgroundColor = UndertaleColor.ToColor(bgColor);
        result.BackgroundBrush = new SolidColorBrush(result.BackgroundColor);

        IImage? GetTexturePageImage(UndertaleTexturePageItem texture)
        {
            if (texture is null)
                return null;
            IImage? image = cache.GetCachedImageFromTexturePageItem(texture);
            if (image is null)
                pending = true;
            return image;
        }

        void Add(SceneItem item)
        {
            result.Items.Add(item);
            if (item.Object is not null)
                result.ItemMap.TryAdd(item.Object, item);
        }

        void AddTile(Tile tile, UndertaleRoom.Layer? layer)
        {
            UndertaleTexturePageItem? texture = tile.Tpag;
            if (texture is null)
                return;
            if (((tile.Color >> 24) & 0xFF) == 0)
                return; // fully transparent (WPF binds element opacity to the color alpha)

            IImage? image = cache.GetCachedImageFromTile(tile);
            if (image is null)
            {
                pending = true;
                return;
            }

            TileItem item = new()
            {
                Object = tile,
                Layer = layer,
                Category = layer is not null ? layer : "Tiles",
                Image = image,
                Tile = tile,
                BaseX = layer?.XOffset ?? 0,
                BaseY = layer?.YOffset ?? 0,
                CompX = Math.Max(0, texture.TargetX - tile.SourceX),
                CompY = Math.Max(0, texture.TargetY - tile.SourceY),
                Opacity = ((tile.Color >> 24) & 0xFF) / 255.0,
            };
            item.Refresh();
            Add(item);
        }

        void AddGameObject(GameObject gameObject, UndertaleRoom.Layer? layer)
        {
            UndertaleSprite? sprite = gameObject.ObjectDefinition?.Sprite;
            if (sprite is null)
                return;

            UndertaleTexturePageItem? texture = sprite.Textures?.ElementAtOrDefault(gameObject.WrappedImageIndex)?.Texture;
            if (texture is null)
                return;
            if (((gameObject.Color >> 24) & 0xFF) == 0)
                return;

            IImage? image = GetTexturePageImage(texture);
            if (image is null)
                return;

            SpriteItem item = new()
            {
                Object = gameObject,
                Layer = null, // instances are never offset by their layer (matches WPF)
                Category = layer is not null ? layer : "GameObjects",
                Image = image,
                GameObject = gameObject,
                SpriteInstanceModel = null,
                Sprite = sprite,
                Texture = texture,
                Opacity = ((gameObject.Color >> 24) & 0xFF) / 255.0,
            };
            item.Refresh();
            Add(item);
        }

        void AddSpriteInstance(UndertaleRoom.SpriteInstance spriteInstance, UndertaleRoom.Layer layer)
        {
            UndertaleSprite? sprite = spriteInstance.Sprite;
            if (sprite is null)
                return;

            UndertaleTexturePageItem? texture = sprite.Textures?.ElementAtOrDefault(spriteInstance.WrappedFrameIndex)?.Texture;
            if (texture is null)
                return;
            if (((spriteInstance.Color >> 24) & 0xFF) == 0)
                return;

            IImage? image = GetTexturePageImage(texture);
            if (image is null)
                return;

            SpriteItem item = new()
            {
                Object = spriteInstance,
                Layer = layer,
                Category = layer,
                Image = image,
                GameObject = null,
                SpriteInstanceModel = spriteInstance,
                Sprite = sprite,
                Texture = texture,
                Opacity = ((spriteInstance.Color >> 24) & 0xFF) / 255.0,
            };
            item.Refresh();
            Add(item);
        }

        void AddParticleSystemInstance(UndertaleRoom.ParticleSystemInstance instance, UndertaleRoom.Layer layer)
        {
            if (((instance.Color >> 24) & 0xFF) == 0)
                return;

            ParticleItem item = new()
            {
                Object = instance,
                Layer = layer,
                Category = layer,
                Instance = instance,
                Opacity = ((instance.Color >> 24) & 0xFF) / 255.0,
            };
            item.Refresh();
            Add(item);
        }

        void AddTilemapLayer(UndertaleRoom.Layer layer)
        {
            Layer.LayerTilesData? tilesData = layer.TilesData;
            UndertaleBackground? tilesBG = tilesData?.Background;
            UndertaleTexturePageItem? texture = tilesBG?.Texture;
            GMImage? pageImage = texture?.TexturePage?.TextureData?.Image;
            if (pageImage is null)
                return;

            IImage? image = cache.GetCachedImageFromGMImage(pageImage);
            if (image is null)
            {
                pending = true;
                return;
            }

            uint maxTileID = 0;
            foreach (UndertaleBackground.TileID tileID in tilesBG!.GMS2TileIds)
                maxTileID = Math.Max(maxTileID, tileID.ID);

            TilemapItem item = new()
            {
                Object = layer,
                Layer = layer,
                Category = layer,
                Page = image,
                SourceX = texture.SourceX,
                SourceY = texture.SourceY,
                TilesX = tilesData.TilesX,
                TilesY = tilesData.TilesY,
                TileColumns = tilesBG.GMS2TileColumns,
                TileWidth = tilesBG.GMS2TileWidth,
                TileHeight = tilesBG.GMS2TileHeight,
                OutputBorderX = tilesBG.GMS2OutputBorderX,
                OutputBorderY = tilesBG.GMS2OutputBorderY,
                MaxTileID = maxTileID,
            };
            item.Refresh();
            Add(item);
        }

        if (!gms2)
        {
            // GMS1: backgrounds, tiles and game objects in list order (same as the WPF
            // "AllObjectsGMS1" composite collection).
            foreach (UndertaleRoom.Background background in room.Backgrounds)
            {
                if (!background.Enabled)
                    continue;

                UndertaleTexturePageItem? texture = background.BackgroundDefinition?.Texture;
                if (texture is null)
                    continue;

                IImage? image = GetTexturePageImage(texture);
                if (image is null)
                    continue;

                background.UpdateStretch();
                double scaleX = background.CalcScaleX;
                double scaleY = background.CalcScaleY;
                if (scaleX == 0 || scaleY == 0)
                    continue;

                double xOffset = background.XOffset;
                double yOffset = background.YOffset;
                bool tiledH = background.TiledHorizontally;
                bool tiledV = background.TiledVertically;

                // WPF: transform = Translate(XOffset, YOffset) -> Scale(CalcScale); on tiled axes
                // the translate is folded into the tile pattern anchor instead.
                BackgroundItem item = new()
                {
                    Object = background,
                    Category = null,
                    Image = image,
                    BaseX = tiledH ? xOffset : xOffset * scaleX,
                    BaseY = tiledV ? yOffset : yOffset * scaleY,
                    PeriodX = texture.SourceWidth * scaleX,
                    PeriodY = texture.SourceHeight * scaleY,
                    SizeX = texture.SourceWidth * scaleX,
                    SizeY = texture.SourceHeight * scaleY,
                    TiledH = tiledH,
                    TiledV = tiledV,
                    RoomWidth = room.Width,
                    RoomHeight = room.Height,
                    Opacity = 1,
                };
                item.Refresh();
                Add(item);
            }

            foreach (Tile tile in room.Tiles)
                AddTile(tile, null);

            foreach (GameObject gameObject in room.GameObjects)
                AddGameObject(gameObject, null);
        }
        else
        {
            if (!room.CheckLayersDepthOrder())
                room.RearrangeLayers();

            // Layers[0] is drawn on top (WPF zIndex), so iterate from the bottom.
            for (int i = room.Layers.Count - 1; i >= 0; i--)
            {
                UndertaleRoom.Layer layer = room.Layers[i];
                if (!layer.IsVisible)
                    continue;

                switch (layer.LayerType)
                {
                    case LayerType.Background:
                    {
                        Layer.LayerBackgroundData? bgData = layer.BackgroundData;
                        if (bgData?.Sprite is null)
                            break; // color-only background: covered by the room background color
                        if (!bgData.Visible)
                            break; // matches the WPF template visibility binding

                        UndertaleTexturePageItem? texture = bgData.Sprite.Textures?.ElementAtOrDefault(0)?.Texture;
                        if (texture is null)
                            break;

                        IImage? image = GetTexturePageImage(texture);
                        if (image is null)
                            break;

                        bgData.UpdateScale();
                        double scaleX = bgData.CalcScaleX;
                        double scaleY = bgData.CalcScaleY;
                        if (scaleX == 0 || scaleY == 0)
                            break;

                        double xOffset = bgData.XOffset; // layer offset + texture target X
                        double yOffset = bgData.YOffset;
                        bool tiledH = bgData.TiledHorizontally;
                        bool tiledV = bgData.TiledVertically;

                        // WPF: transform = Scale(CalcScale) -> Translate(XOffset); on tiled axes
                        // the anchor is the raw layer offset (the texture target offset is dropped).
                        BackgroundItem item = new()
                        {
                            Object = bgData,
                            Category = layer,
                            Image = image,
                            BaseX = tiledH ? layer.XOffset : xOffset,
                            BaseY = tiledV ? layer.YOffset : yOffset,
                            PeriodX = texture.SourceWidth * scaleX,
                            PeriodY = texture.SourceHeight * scaleY,
                            SizeX = texture.SourceWidth * scaleX,
                            SizeY = texture.SourceHeight * scaleY,
                            TiledH = tiledH,
                            TiledV = tiledV,
                            RoomWidth = room.Width,
                            RoomHeight = room.Height,
                            Opacity = ((bgData.Color >> 24) & 0xFF) / 255.0,
                        };
                        item.Refresh();
                        if (item.Valid)
                            Add(item);
                        break;
                    }
                    case LayerType.Instances:
                    {
                        foreach (GameObject gameObject in layer.InstancesData.Instances)
                            AddGameObject(gameObject, layer);
                        break;
                    }
                    case LayerType.Assets:
                    {
                        if (layer.AssetsData.LegacyTiles is not null)
                            foreach (Tile tile in layer.AssetsData.LegacyTiles)
                                AddTile(tile, layer);
                        if (layer.AssetsData.Sprites is not null)
                            foreach (UndertaleRoom.SpriteInstance spriteInstance in layer.AssetsData.Sprites)
                                AddSpriteInstance(spriteInstance, layer);
                        if (layer.AssetsData.ParticleSystems is not null)
                            foreach (UndertaleRoom.ParticleSystemInstance instance in layer.AssetsData.ParticleSystems)
                                AddParticleSystemInstance(instance, layer);
                        break;
                    }
                    case LayerType.Tiles:
                        AddTilemapLayer(layer);
                        break;
                }
            }
        }

        pendingImages = pending;
        return result;
    }
}

/// <summary>
/// Shared pens and brushes for the room editor canvas and its scene items.
/// </summary>
internal static class RoomEditorAssets
{
    public static readonly IBrush BackdropBrush = Brushes.Gray;
    public static readonly IBrush GridBrush = new SolidColorBrush(Color.FromRgb(128, 128, 128));
    public static readonly IBrush MarkerBrush = new SolidColorBrush(Color.FromRgb(192, 192, 192)); // Silver
    public static readonly Pen RoomBorderPen = new(Brushes.White, 1);
    public static readonly Pen MarkerPen = new(Brushes.White, 1);
    public static readonly Pen ParticleBorderPen = new(Brushes.DarkCyan, 1);
}

/// <summary>
/// A single drawable room element.
/// </summary>
internal abstract class SceneItem
{
    public object Object = null!;                 // model object (selection identity)
    public UndertaleRoom.Layer? Layer;            // owning layer (GMS2)
    public object? Category;                      // hit-test filter category (layer, "GameObjects", "Tiles", null)
    public Rect Bounds;                           // axis-aligned bounds in room coords (culling & focusing)
    public bool Valid = true;
    public Vector LayerOffset;                    // room-space offset the layer adds to the model position

    public abstract void Draw(DrawingContext context, Rect viewport);
    public abstract bool HitTest(Point roomPoint);
    public abstract Point[] OutlineCorners();

    /// <summary>Recomputes the geometry from the referenced model objects.</summary>
    public virtual void Refresh() { }

    public virtual Rect HitRect() => Bounds;

    public abstract (int X, int Y) GetModelPosition();
    public abstract void SetModelPosition(int x, int y);

    protected static Rect TransformBounds(Matrix matrix, Rect rect)
    {
        Point[] corners = TransformCorners(matrix, rect);
        double minX = corners[0].X, maxX = corners[0].X, minY = corners[0].Y, maxY = corners[0].Y;
        for (int i = 1; i < 4; i++)
        {
            minX = Math.Min(minX, corners[i].X);
            maxX = Math.Max(maxX, corners[i].X);
            minY = Math.Min(minY, corners[i].Y);
            maxY = Math.Max(maxY, corners[i].Y);
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    protected static Point[] TransformCorners(Matrix matrix, Rect rect)
    {
        return
        [
            new Point(rect.X, rect.Y).Transform(matrix),
            new Point(rect.Right, rect.Y).Transform(matrix),
            new Point(rect.Right, rect.Bottom).Transform(matrix),
            new Point(rect.X, rect.Bottom).Transform(matrix),
        ];
    }
}

/// <summary>
/// A tiled or single-copy background image (GMS1 <see cref="UndertaleRoom.Background"/> and
/// GMS2 <see cref="Layer.LayerBackgroundData"/>).
/// BaseX/BaseY is the room-space position of the first copy; copies repeat with PeriodX/PeriodY
/// on tiled axes and are clipped to the room.
/// </summary>
internal sealed class BackgroundItem : SceneItem
{
    public IImage Image = null!;
    public double BaseX, BaseY;
    public double PeriodX, PeriodY;
    public double SizeX, SizeY;
    public bool TiledH, TiledV;
    public uint RoomWidth, RoomHeight;
    public double Opacity = 1;

    public override void Refresh()
    {
        Valid = PeriodX > 0 && PeriodY > 0 && SizeX > 0 && SizeY > 0;
        if (!Valid)
        {
            Bounds = default;
            return;
        }

        if (TiledH || TiledV)
            Bounds = new Rect(0, 0, RoomWidth, RoomHeight);
        else
            Bounds = new Rect(BaseX, BaseY, SizeX, SizeY);
    }

    public override void Draw(DrawingContext context, Rect viewport)
    {
        if (!Valid)
            return;

        using var opacity = Opacity < 1 ? context.PushOpacity(Opacity) : default;
        using var clip = (TiledH || TiledV) ? context.PushClip(new Rect(0, 0, RoomWidth, RoomHeight)) : default;

        double loX = TiledH ? Math.Max(viewport.X, 0) : BaseX;
        double hiX = TiledH ? Math.Min(viewport.Right, RoomWidth) : BaseX + SizeX;
        double loY = TiledV ? Math.Max(viewport.Y, 0) : BaseY;
        double hiY = TiledV ? Math.Min(viewport.Bottom, RoomHeight) : BaseY + SizeY;

        int startX = TiledH ? (int)Math.Ceiling((loX - BaseX) / PeriodX - 1e-6) : 0;
        int endX = TiledH ? (int)Math.Floor((hiX - BaseX) / PeriodX + 1e-6) : 0;
        int startY = TiledV ? (int)Math.Ceiling((loY - BaseY) / PeriodY - 1e-6) : 0;
        int endY = TiledV ? (int)Math.Floor((hiY - BaseY) / PeriodY + 1e-6) : 0;

        for (int iy = startY; iy <= endY; iy++)
        {
            double y = BaseY + iy * PeriodY;
            for (int ix = startX; ix <= endX; ix++)
            {
                double x = BaseX + ix * PeriodX;
                context.DrawImage(Image, new Rect(x, y, SizeX, SizeY));
            }
        }
    }

    public override bool HitTest(Point roomPoint)
    {
        double loX = TiledH ? 0 : BaseX;
        double hiX = TiledH ? RoomWidth : BaseX + SizeX;
        double loY = TiledV ? 0 : BaseY;
        double hiY = TiledV ? RoomHeight : BaseY + SizeY;
        if (roomPoint.X < loX || roomPoint.X > hiX || roomPoint.Y < loY || roomPoint.Y > hiY)
            return false;

        int startX = TiledH ? (int)Math.Ceiling((loX - BaseX) / PeriodX - 1e-6) : 0;
        int endX = TiledH ? (int)Math.Floor((hiX - BaseX) / PeriodX + 1e-6) : 0;
        int startY = TiledV ? (int)Math.Ceiling((loY - BaseY) / PeriodY - 1e-6) : 0;
        int endY = TiledV ? (int)Math.Floor((hiY - BaseY) / PeriodY + 1e-6) : 0;

        for (int iy = startY; iy <= endY; iy++)
        {
            double y = BaseY + iy * PeriodY;
            for (int ix = startX; ix <= endX; ix++)
            {
                double x = BaseX + ix * PeriodX;
                if (roomPoint.X >= x && roomPoint.X < x + SizeX && roomPoint.Y >= y && roomPoint.Y < y + SizeY)
                    return true;
            }
        }

        return false;
    }

    public override Point[] OutlineCorners()
    {
        double loX = TiledH ? Math.Max(Bounds.X, 0) : BaseX;
        double loY = TiledV ? Math.Max(Bounds.Y, 0) : BaseY;
        return
        [
            new Point(loX, loY),
            new Point(loX + SizeX, loY),
            new Point(loX + SizeX, loY + SizeY),
            new Point(loX, loY + SizeY),
        ];
    }

    // Backgrounds are not draggable on the canvas; these only satisfy the item contract.
    public override (int X, int Y) GetModelPosition() => ((int)BaseX, (int)BaseY);

    public override void SetModelPosition(int x, int y)
    {
    }
}

/// <summary>
/// A sprite drawn at its instance position with the sprite origin as the scale/rotation pivot -
/// identical to the WPF game object / sprite instance templates.
/// </summary>
internal sealed class SpriteItem : SceneItem
{
    public IImage Image = null!;
    public GameObject? GameObject;
    public UndertaleRoom.SpriteInstance? SpriteInstanceModel;
    public UndertaleSprite Sprite = null!;
    public UndertaleTexturePageItem Texture = null!;
    public double Opacity = 1;

    Matrix transform;
    Rect localRect; // image rect in local (pre-transform) space

    public override void Refresh()
    {
        Valid = false;

        double x, y, scaleX, scaleY, rotation;
        if (GameObject is { } gameObject)
        {
            x = gameObject.X;
            y = gameObject.Y;
            scaleX = gameObject.ScaleX;
            scaleY = gameObject.ScaleY;
            rotation = gameObject.Rotation;
            LayerOffset = default;
        }
        else if (SpriteInstanceModel is { } spriteInstance)
        {
            LayerOffset = new Vector(Layer?.XOffset ?? 0, Layer?.YOffset ?? 0);
            x = LayerOffset.X + spriteInstance.X;
            y = LayerOffset.Y + spriteInstance.Y;
            scaleX = spriteInstance.ScaleX;
            scaleY = spriteInstance.ScaleY;
            rotation = spriteInstance.Rotation;
        }
        else
        {
            return;
        }

        if (scaleX == 0 || scaleY == 0)
            return;
        if (Texture.SourceWidth == 0 || Texture.SourceHeight == 0)
            return;

        // WPF net transform: the sprite origin lands at (x, y); scale and rotation are applied
        // around it. The image is drawn at (TargetX - OriginX, TargetY - OriginY) in local space.
        transform = Matrix.CreateScale(scaleX, scaleY)
                  * Matrix.CreateRotation(-rotation * (Math.PI / 180.0))
                  * Matrix.CreateTranslation(x, y);
        localRect = new Rect(Texture.TargetX - Sprite.OriginX, Texture.TargetY - Sprite.OriginY,
            Texture.SourceWidth, Texture.SourceHeight);
        Bounds = TransformBounds(transform, localRect);
        Valid = true;
    }

    public override void Draw(DrawingContext context, Rect viewport)
    {
        if (!Valid)
            return;

        using var opacity = Opacity < 1 ? context.PushOpacity(Opacity) : default;
        using var transformPush = context.PushTransform(transform);
        context.DrawImage(Image, localRect);
    }

    public override bool HitTest(Point roomPoint)
    {
        if (!Valid || !transform.TryInvert(out Matrix inverse))
            return false;
        return localRect.Contains(roomPoint.Transform(inverse));
    }

    public override Rect HitRect() => Valid ? TransformBounds(transform, localRect) : default;

    public override (int X, int Y) GetModelPosition()
        => GameObject is { } gameObject ? (gameObject.X, gameObject.Y) : (SpriteInstanceModel!.X, SpriteInstanceModel!.Y);

    public override void SetModelPosition(int x, int y)
    {
        if (GameObject is { } gameObject)
        {
            gameObject.X = x;
            gameObject.Y = y;
        }
        else if (SpriteInstanceModel is { } spriteInstance)
        {
            spriteInstance.X = x;
            spriteInstance.Y = y;
        }
    }

    public override Point[] OutlineCorners() => Valid ? TransformCorners(transform, localRect) :
    [
        default, default, default, default
    ];
}

/// <summary>
/// A legacy tile (GMS1 room tile / GMS2 asset layer tile) drawn 1:1 at its position, scaled
/// around its top-left corner - identical to the WPF tile template.
/// </summary>
internal sealed class TileItem : SceneItem
{
    public IImage Image = null!;
    public Tile Tile = null!;
    public double BaseX, BaseY;      // layer offset
    public double CompX, CompY;      // crop compensation when the tile source starts before the texture page item
    public double Opacity = 1;

    Matrix transform;
    Rect hitRect;

    public override void Refresh()
    {
        Valid = false;

        double scaleX = Tile.ScaleX;
        double scaleY = Tile.ScaleY;
        if (scaleX == 0 || scaleY == 0 || Tile.Width == 0 || Tile.Height == 0)
            return;
        if (Image.Size.Width <= 0 || Image.Size.Height <= 0)
            return;

        LayerOffset = new Vector(BaseX, BaseY);
        transform = Matrix.CreateScale(scaleX, scaleY) * Matrix.CreateTranslation(BaseX + Tile.X, BaseY + Tile.Y);
        hitRect = new Rect(0, 0, Tile.Width, Tile.Height);
        Bounds = TransformBounds(transform, hitRect);
        Valid = true;
    }

    public override void Draw(DrawingContext context, Rect viewport)
    {
        if (!Valid)
            return;

        using var opacity = Opacity < 1 ? context.PushOpacity(Opacity) : default;
        using var transformPush = context.PushTransform(transform);
        context.DrawImage(Image, new Rect(CompX, CompY, Image.Size.Width, Image.Size.Height));
    }

    public override bool HitTest(Point roomPoint)
    {
        if (!Valid || !transform.TryInvert(out Matrix inverse))
            return false;
        return hitRect.Contains(roomPoint.Transform(inverse));
    }

    public override Rect HitRect() => Valid ? TransformBounds(transform, hitRect) : default;

    public override (int X, int Y) GetModelPosition() => (Tile.X, Tile.Y);

    public override void SetModelPosition(int x, int y)
    {
        Tile.X = x;
        Tile.Y = y;
    }

    public override Point[] OutlineCorners() => Valid ? TransformCorners(transform, hitRect) :
    [
        default, default, default, default
    ];
}

/// <summary>
/// A GMS2 tilemap layer drawn per tile from the tileset texture page, honoring the GameMaker
/// orientation flags (flip X/Y, rotate) with the same final mapping as the WPF editor.
/// Tile data is read live from the layer so painting doesn't require scene rebuilds.
/// </summary>
internal sealed class TilemapItem : SceneItem
{
    public IImage Page = null!;
    public ushort SourceX, SourceY;
    public uint TilesX, TilesY, TileColumns, MaxTileID;
    public uint TileWidth, TileHeight, OutputBorderX, OutputBorderY;

    public override void Refresh()
    {
        Valid = TileWidth > 0 && TileHeight > 0 && Page.Size.Width > 0;
        Bounds = Valid ? new Rect(Layer.XOffset, Layer.YOffset, TileWidth * TilesX, TileHeight * TilesY) : default;
    }

    public override void Draw(DrawingContext context, Rect viewport)
    {
        if (!Valid || Layer.TilesData?.TileData is not { } tileData || tileData.Length == 0)
            return;

        uint tilesX = Layer.TilesData.TilesX;
        uint tilesY = Layer.TilesData.TilesY;
        if (tilesX == 0 || tilesY == 0)
            return;

        double mapX = Layer.XOffset;
        double mapY = Layer.YOffset;

        int x0 = (int)Math.Max(0, Math.Floor((viewport.X - mapX) / TileWidth));
        int y0 = (int)Math.Max(0, Math.Floor((viewport.Y - mapY) / TileHeight));
        int x1 = (int)Math.Min(tilesX - 1, Math.Floor((viewport.Right - mapX) / TileWidth));
        int y1 = (int)Math.Min(tilesY - 1, Math.Floor((viewport.Bottom - mapY) / TileHeight));
        if (x1 < x0 || y1 < y0)
            return;

        double pageWidth = Page.Size.Width;
        double pageHeight = Page.Size.Height;

        for (int y = y0; y <= y1; y++)
        {
            uint[]? row = y < tileData.Length ? tileData[y] : null;
            if (row is null)
                continue;

            int rowLimit = Math.Min(x1, Math.Min(row.Length - 1, (int)tilesX - 1));
            for (int x = x0; x <= rowLimit; x++)
            {
                uint tile = row[x];
                uint tileID = tile & UndertaleRoomViewModel.TILE_ID;
                if (tileID == 0)
                    continue;

                uint tileOrientation = tile >> 28;
                if (tileID > MaxTileID)
                {
                    tileID &= 0x0FFFFFFF; // remove tile flag
                    if (tileID > MaxTileID)
                        continue;
                }

                uint tileX = tileID % TileColumns;
                uint tileY = tileID / TileColumns;

                double sourceX = SourceX + (tileX * (TileWidth + OutputBorderX * 2) + OutputBorderX);
                double sourceY = SourceY + (tileY * (TileHeight + OutputBorderY * 2) + OutputBorderY);
                if (sourceX < 0 || sourceY < 0 || sourceX + TileWidth > pageWidth || sourceY + TileHeight > pageHeight)
                    continue;

                Matrix orientation = OrientationToMatrix(tileOrientation, TileWidth, TileHeight);
                using var _ = context.PushTransform(orientation * Matrix.CreateTranslation(mapX + x * TileWidth, mapY + y * TileHeight));
                context.DrawImage(Page, new Rect(sourceX, sourceY, TileWidth, TileHeight), new Rect(0, 0, TileWidth, TileHeight));
            }
        }
    }

    public override bool HitTest(Point roomPoint)
    {
        // Matches the WPF TileLayerImage hit testing: only cells with a non-zero tile are hit.
        if (!Valid || Layer.TilesData?.TileData is not { } tileData)
            return false;

        double mapX = Layer.XOffset;
        double mapY = Layer.YOffset;

        int x = (int)Math.Floor((roomPoint.X - mapX) / TileWidth);
        int y = (int)Math.Floor((roomPoint.Y - mapY) / TileHeight);
        if (x < 0 || y < 0 || y >= tileData.Length || x >= tileData[y].Length)
            return false;

        return tileData[y][x] != 0;
    }

    public override Point[] OutlineCorners()
    {
        return
        [
            new Point(Bounds.X, Bounds.Y),
            new Point(Bounds.Right, Bounds.Y),
            new Point(Bounds.Right, Bounds.Bottom),
            new Point(Bounds.X, Bounds.Bottom),
        ];
    }

    // Tilemap layers are not draggable on the canvas; these only satisfy the item contract.
    public override (int X, int Y) GetModelPosition() => (0, 0);

    public override void SetModelPosition(int x, int y)
    {
    }

    // Final per-tile mapping of the 8 GameMaker orientation flag combinations, matching the WPF
    // editor (RotateFlip on the tile bitmap / the tile rectangle transform group).
    static Matrix OrientationToMatrix(uint orientation, double w, double h) => orientation switch
    {
        1 => new Matrix(-1, 0, 0, 1, w, 0),   // flip X:          (w-x, y)
        2 => new Matrix(1, 0, 0, -1, 0, h),   // flip Y:          (x, h-y)
        3 => new Matrix(-1, 0, 0, -1, w, h),  // flip XY:         (w-x, h-y)
        4 => new Matrix(0, 1, -1, 0, w, 0),   // rotate 90 CW:    (w-y, x)
        5 => new Matrix(0, -1, -1, 0, w, h),  // rot90 CW + flipY: (w-y, h-x)
        6 => new Matrix(0, 1, 1, 0, 0, 0),    // rot90 CW + flipX: (y, x)
        7 => new Matrix(0, -1, 1, 0, 0, h),   // rot90 CCW:       (y, h-x)
        _ => Matrix.Identity,
    };
}

/// <summary>
/// A particle system instance marker (16x16 icon + emitter region outline), transformed exactly
/// like the WPF particle system template.
/// </summary>
internal sealed class ParticleItem : SceneItem
{
    public UndertaleRoom.ParticleSystemInstance Instance = null!;
    public double Opacity = 1;

    Matrix transform;
    Rect borderRect;

    public override void Refresh()
    {
        Valid = false;

        LayerOffset = new Vector(Layer?.XOffset ?? 0, Layer?.YOffset ?? 0);
        double x = LayerOffset.X + Instance.X;
        double y = LayerOffset.Y + Instance.Y;

        if (Instance.ScaleX == 0 || Instance.ScaleY == 0)
            return;

        borderRect = ComputeEmitterRect(Instance.ParticleSystem);

        // WPF transform chain: Translate(-8,-8), Translate(X,Y), Scale, Rotate about (X,Y).
        transform = Matrix.CreateTranslation(x - 8, y - 8)
                  * Matrix.CreateScale(Instance.ScaleX, Instance.ScaleY)
                  * Matrix.CreateTranslation(-x, -y)
                  * Matrix.CreateRotation(-Instance.Rotation * (Math.PI / 180.0))
                  * Matrix.CreateTranslation(x, y);

        Rect markerRect = TransformBounds(transform, new Rect(0, 0, 16, 16));
        Rect borderBounds = TransformBounds(transform, borderRect);
        Bounds = markerRect.Union(borderBounds);
        Valid = true;
    }

    public override void Draw(DrawingContext context, Rect viewport)
    {
        if (!Valid)
            return;

        using var opacity = Opacity < 1 ? context.PushOpacity(Opacity) : default;
        using var transformPush = context.PushTransform(transform);

        context.DrawRectangle(RoomEditorAssets.MarkerBrush, null, new Rect(0, 0, 16, 16), 8, 8);
        context.DrawRectangle(Brushes.White, null, new Rect(5, 5, 3, 3), 1.5, 1.5);
        context.DrawLine(RoomEditorAssets.MarkerPen, new Point(3.5, 6.5), new Point(9.5, 6.5));
        context.DrawLine(RoomEditorAssets.MarkerPen, new Point(6.5, 3.5), new Point(6.5, 9.5));
        context.DrawLine(RoomEditorAssets.MarkerPen, new Point(8, 10), new Point(12, 10));
        context.DrawLine(RoomEditorAssets.MarkerPen, new Point(10, 8), new Point(10, 12));

        context.DrawRectangle(null, RoomEditorAssets.ParticleBorderPen, borderRect);
    }

    public override bool HitTest(Point roomPoint)
    {
        if (!Valid || !transform.TryInvert(out Matrix inverse))
            return false;
        return new Rect(0, 0, 16, 16).Contains(roomPoint.Transform(inverse));
    }

    public override Rect HitRect() => Valid ? TransformBounds(transform, new Rect(0, 0, 16, 16)) : default;

    public override (int X, int Y) GetModelPosition() => (Instance.X, Instance.Y);

    public override void SetModelPosition(int x, int y)
    {
        Instance.X = x;
        Instance.Y = y;
    }

    public override Point[] OutlineCorners() => Valid ? TransformCorners(transform, new Rect(0, 0, 16, 16)) :
    [
        default, default, default, default
    ];

    // Port of the WPF "ParticleSystemRectConverter".
    static Rect ComputeEmitterRect(UndertaleParticleSystem? particleSystem)
    {
        if (particleSystem?.Emitters is not { Count: > 0 } emitters)
            return new Rect(8, 8, 0, 0);

        IEnumerable<UndertaleParticleSystemEmitter> resources = emitters
            .Select(x => x.Resource)
            .Where(x => x is not null)
            .Select(x => x!);
        if (!resources.Any())
            return new Rect(8, 8, 0, 0);

        float minX = resources.Select(x => x.RegionX).Min();
        float maxX = resources.Select(x => x.RegionX + x.RegionWidth).Max();
        float width = Math.Abs(minX - maxX);

        float minY = resources.Select(x => x.RegionY).Min();
        float maxY = resources.Select(x => x.RegionY + x.RegionHeight).Max();
        float height = Math.Abs(minY - maxY);

        float x = resources.Select(x => x.RegionX - x.RegionWidth * 0.5f).Min();
        float y = resources.Select(x => x.RegionY - x.RegionHeight * 0.5f).Min();

        return new Rect(x + 8, y + 8, width, height);
    }
}
