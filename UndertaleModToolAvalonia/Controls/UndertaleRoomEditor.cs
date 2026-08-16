using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Xaml.Interactions.DragAndDrop;
using Avalonia.Xaml.Interactivity;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace UndertaleModToolAvalonia;

public class UndertaleRoomEditor : Control
{
    public record RoomItem(
        object Object,
        UndertaleRoom.Layer? Layer = null,
        RoomItemSelectable? Selectable = null
    );
    public record RoomItemProperties(int X, int Y);
    public record RoomItemSelectable(
        object Category,
        Rect Bounds,
        double Rotation,
        Point Pivot,
        Func<RoomItemProperties> GetProperties,
        Action<RoomItemProperties> SetProperties
    );

    enum InteractionMode
    {
        Items,
        Tiles,
        RoomTiles,
    }

    UndertaleRoomViewModel? vm;

    readonly RoomRenderer rendererInstance = new();

    double customDrawOperationTime;

    // Room controls
    Vector translation = new(0, 0);
    double scaling = 1;

    bool translationMoving = false;
    bool translationHasMoved = false;
    Point translationMoveOffset = new(0, 0);

    Point pointerPosition;
    Point pointerPositionInRoom;

    Point itemMoveOffset = new(0, 0);

    object? hoveredItem;

    uint? hoveredTile = null;

    #region Touch

    enum TouchMode
    {
        None,
        PossibleTap,
        Panning,
        MovingItem,
        Pinching,
    }

    static readonly TimeSpan TouchLongPressDuration = TimeSpan.FromSeconds(2);
    const double TouchMoveThreshold = 10;

    readonly Dictionary<long, Point> touchPoints = new();
    TouchMode touchMode = TouchMode.None;
    long? touchPrimaryId = null;
    long? touchSecondaryId = null;
    Point touchStartPosition;
    DispatcherTimer? longPressTimer;

    double pinchStartDistance;
    double pinchStartScale;
    Point pinchStartRoomPoint;

    #endregion

    public UndertaleRoomEditor()
    {
        ClipToBounds = true;
        Focusable = true;

        Interaction.SetBehaviors(this, [new ContextDropBehavior() { Handler = new UndertaleReferenceDropHandler() }]);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        vm = (DataContext as UndertaleRoomViewModel)!;
        vm?.Room.SetupRoom();

        translation = new(0, 0);
        scaling = 1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Touch)
        {
            TouchPressed(e);
            e.Handled = true;
            return;
        }

        PointerPoint pointerPoint = e.GetCurrentPoint(this);
        InteractionMode interactionMode = GetInteractionMode();

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        var roomItems = Updater.MakeRoomItems(vm!.Room);

        if (pointerPoint.Properties.IsMiddleButtonPressed
            || (interactionMode == InteractionMode.Items && pointerPoint.Properties.IsRightButtonPressed))
        {
            TranslationMoveOnPressed();
        }

        if (interactionMode == InteractionMode.Items)
        {
            if (pointerPoint.Properties.IsLeftButtonPressed)
            {
                ItemMoveOnPressed(roomItems);
            }
        }
        else if (interactionMode == InteractionMode.Tiles)
        {
            UndertaleRoom.Layer? tilesLayer = GetSelectedTilesLayer();
            if (tilesLayer is not null)
            {
                if (pointerPoint.Properties.IsLeftButtonPressed)
                {
                    SetLayerTileAtPointer(tilesLayer, vm!.SelectedTileData);
                }
                else if (pointerPoint.Properties.IsRightButtonPressed)
                {
                    SetLayerTileAtPointer(tilesLayer, 0);
                }
            }
        }
        else if (interactionMode == InteractionMode.RoomTiles)
        {
            if (pointerPoint.Properties.IsLeftButtonPressed)
            {
                SetRoomTileAtPointer(roomItems, vm!.SelectedTileResource, vm!.SelectedTileSourceRect, overrideGrid: shift);
            }
            else if (pointerPoint.Properties.IsRightButtonPressed)
            {
                RemoveRoomTileAtPointer(roomItems);
            }
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

        PointerPoint pointerPoint = e.GetCurrentPoint(this);
        InteractionMode interactionMode = GetInteractionMode();
        UndertaleRoom.Layer? tilesLayer = GetSelectedTilesLayer();

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        var roomItems = Updater.MakeRoomItems(vm!.Room);

        pointerPosition = e.GetPosition(this);
        pointerPositionInRoom = (pointerPosition - translation) / scaling;

        TranslationMoveOnMoved();

        if (interactionMode == InteractionMode.Items)
        {
            if (pointerPoint.Properties.IsLeftButtonPressed)
            {
                ItemMoveOnMoved(roomItems, overrideGrid: shift);
                roomItems = Updater.MakeRoomItems(vm!.Room);
            }
        }
        else if (interactionMode == InteractionMode.Tiles)
        {
            if (tilesLayer is not null)
            {
                if (pointerPoint.Properties.IsLeftButtonPressed)
                {
                    SetLayerTileAtPointer(tilesLayer, vm!.SelectedTileData);
                }
                else if (pointerPoint.Properties.IsRightButtonPressed)
                {
                    SetLayerTileAtPointer(tilesLayer, 0);
                }
            }
        }
        else if (interactionMode == InteractionMode.RoomTiles)
        {
            if (pointerPoint.Properties.IsLeftButtonPressed)
            {
                // TODO: Add dragging that respects size of tile
                SetRoomTileAtPointer(roomItems, vm!.SelectedTileResource, vm!.SelectedTileSourceRect, overrideGrid: shift);
            }
            else if (pointerPoint.Properties.IsRightButtonPressed)
            {
                RemoveRoomTileAtPointer(roomItems);
            }
        }

        hoveredItem = null;
        hoveredTile = null;

        if (interactionMode == InteractionMode.Items)
        {
            ItemHoverOnMoved(roomItems);
        }
        else if (interactionMode == InteractionMode.Tiles)
        {
            if (tilesLayer is not null)
            {
                hoveredTile = GetLayerTileAtPointer(tilesLayer);
            }
        }
        else if (interactionMode == InteractionMode.RoomTiles)
        {
            hoveredItem = GetRoomTileAtPointer(roomItems);
        }

        vm!.StatusText = $"({Math.Floor(pointerPositionInRoom.X)}, {Math.Floor(pointerPositionInRoom.Y)})";
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Touch)
        {
            TouchReleased(e);
            e.Handled = true;
            return;
        }

        InteractionMode interactionMode = GetInteractionMode();
        UndertaleRoom.Layer? tilesLayer = GetSelectedTilesLayer();

        if (interactionMode == InteractionMode.Tiles)
        {
            if (tilesLayer is not null)
            {
                if (e.InitialPressMouseButton == MouseButton.Middle)
                {
                    if (!translationHasMoved)
                    {
                        uint? tile = GetLayerTileAtPointer(tilesLayer);
                        if (tile is not null)
                            vm!.SelectedTileData = (uint)tile;
                    }
                }
            }
        }
        else if (interactionMode == InteractionMode.RoomTiles)
        {
            if (e.InitialPressMouseButton == MouseButton.Middle)
            {
                if (!translationHasMoved)
                {
                    var roomItems = Updater.MakeRoomItems(vm!.Room);

                    UndertaleRoom.Tile? tile = GetRoomTileAtPointer(roomItems);
                    if (tile is not null)
                    {
                        vm!.SelectedTileResource = tile.ObjectDefinition;
                        vm!.SelectedTileSourceRect = new(tile.SourceX, tile.SourceY, tile.Width, tile.Height);
                    }
                }
            }
        }

        TranslationMoveOnReleased();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.Delta.Y > 0)
        {
            translation *= 2;
            translation -= pointerPosition;
            scaling *= 2;
        }
        else if (e.Delta.Y < 0)
        {
            scaling /= 2;
            translation += pointerPosition;
            translation /= 2;
        }

        translation = new Vector(Math.Round(translation.X), Math.Round(translation.Y));

        vm!.Zoom = scaling;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.PhysicalKey == PhysicalKey.Space)
        {
            TranslationMoveOnPressed();
        }
        else if (e.PhysicalKey == PhysicalKey.F)
        {
            var roomItems = Updater.MakeRoomItems(vm!.Room);
            FocusOnSelectedItem(roomItems);
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.PhysicalKey == PhysicalKey.Space)
        {
            TranslationMoveOnReleased();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (e.Pointer.Type == PointerType.Touch)
        {
            ResetTouchState();
        }
    }

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
            StopLongPressTimer();
            BeginPinch();
        }
    }

    void TouchMoved(PointerEventArgs e)
    {
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
                break;
            case TouchMode.MovingItem:
                TouchMoveAction();
                break;
        }

        vm!.StatusText = $"({Math.Floor(pointerPositionInRoom.X)}, {Math.Floor(pointerPositionInRoom.Y)})";
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
                    pointerPosition = remaining.Value;
                    TranslationMoveOnPressed();
                }
                else
                {
                    touchMode = TouchMode.None;
                }
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
        if (touchPoints.Count < 2)
            return;

        var points = touchPoints.Values.Take(2).ToArray();
        double newDist = Distance(points[0], points[1]);
        if (newDist <= 0)
            return;

        double factor = newDist / pinchStartDistance;
        double newScale = Math.Clamp(pinchStartScale * factor, 0.001, 1000);

        Point mid = new((points[0].X + points[1].X) / 2, (points[0].Y + points[1].Y) / 2);

        translation = mid - pinchStartRoomPoint * newScale;
        translation = new Vector(Math.Round(translation.X), Math.Round(translation.Y));

        scaling = newScale;
        vm!.Zoom = scaling;
    }

    void TouchPressAction()
    {
        var roomItems = Updater.MakeRoomItems(vm!.Room);
        InteractionMode interactionMode = GetInteractionMode();

        switch (interactionMode)
        {
            case InteractionMode.Items:
                ItemHoverOnMoved(roomItems);
                ItemMoveOnPressed(roomItems);
                break;
            case InteractionMode.Tiles:
                UndertaleRoom.Layer? tilesLayer = GetSelectedTilesLayer();
                if (tilesLayer is not null && !vm!.IsLocked)
                    SetLayerTileAtPointer(tilesLayer, vm!.SelectedTileData);
                break;
            case InteractionMode.RoomTiles:
                if (!vm!.IsLocked)
                    SetRoomTileAtPointer(roomItems, vm!.SelectedTileResource, vm!.SelectedTileSourceRect, overrideGrid: false);
                break;
        }
    }

    void TouchMoveAction()
    {
        if (vm!.IsLocked)
            return;

        var roomItems = Updater.MakeRoomItems(vm!.Room);
        InteractionMode interactionMode = GetInteractionMode();

        switch (interactionMode)
        {
            case InteractionMode.Items:
                ItemMoveOnMoved(roomItems, overrideGrid: false);
                break;
            case InteractionMode.Tiles:
                UndertaleRoom.Layer? tilesLayer = GetSelectedTilesLayer();
                if (tilesLayer is not null)
                    SetLayerTileAtPointer(tilesLayer, vm!.SelectedTileData);
                break;
            case InteractionMode.RoomTiles:
                SetRoomTileAtPointer(roomItems, vm!.SelectedTileResource, vm!.SelectedTileSourceRect, overrideGrid: false);
                break;
        }
    }

    public override void Render(DrawingContext context)
    {
        if (IsEffectivelyVisible && vm is not null)
        {
            scaling = vm.Zoom;

            Color selectedColor = GetSelectedColor();

            Stopwatch stopWatch = new();
            stopWatch.Start();

            context.FillRectangle(Brushes.Gray, new Rect(0, 0, Bounds.Width, Bounds.Height));

            uint roomWidth = vm.Room.Width;
            uint roomHeight = vm.Room.Height;

            context.DrawRectangle(new Pen(Brushes.White, 1), new Rect(translation.X - 1, translation.Y - 1,
                Math.Ceiling(roomWidth * scaling + 1), Math.Ceiling(roomHeight * scaling + 1)), 0);

            using (context.PushTransform(Matrix.CreateTranslation(translation.X, translation.Y)))
            using (context.PushTransform(Matrix.CreateScale(scaling, scaling)))
            {
                var roomItems = Updater.MakeRoomItems(vm.Room);
                var renderCommands = new RoomRenderer.RenderCommandsBuilder(vm.Room).RenderCommands;

                rendererInstance.RenderCommands(renderCommands, context);

                if (vm.IsGridEnabled)
                {
                    if (vm.GridWidth * scaling >= 2)
                        for (uint x = 0; x < roomWidth; x += vm.GridWidth)
                        {
                            context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)), 1),
                                new Point(x, 0), new Point(x, roomHeight));
                        }

                    if (vm.GridHeight * scaling >= 2)
                        for (uint y = 0; y < roomHeight; y += vm.GridHeight)
                        {
                            context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)), 1),
                                new Point(0, y), new Point(roomWidth, y));
                        }
                }

                RoomItem? selectedRoomItem = GetSelectedRoomItem(roomItems);
                RoomItem? hoveredRoomItem = GetRoomItemOfItem(roomItems, hoveredItem);

                if (selectedRoomItem is not null && selectedRoomItem.Selectable is not null)
                {
                    Rect rect = selectedRoomItem.Selectable.Bounds;

                    using (context.PushTransform(
                        Matrix.CreateTranslation(selectedRoomItem.Selectable.Pivot.X, selectedRoomItem.Selectable.Pivot.Y) *
                        Matrix.CreateRotation(-selectedRoomItem.Selectable.Rotation * (Math.PI / 180.0)) *
                        Matrix.CreateTranslation(-selectedRoomItem.Selectable.Pivot.X, -selectedRoomItem.Selectable.Pivot.Y)))
                    {
                        context.DrawRectangle(new Pen(new SolidColorBrush(selectedColor), 2 / scaling), rect, 0);
                    }
                }

                if (hoveredRoomItem is not null && hoveredRoomItem.Selectable is not null)
                {
                    Rect rect = hoveredRoomItem.Selectable.Bounds;

                    using (context.PushTransform(
                        Matrix.CreateTranslation(hoveredRoomItem.Selectable.Pivot.X, hoveredRoomItem.Selectable.Pivot.Y) *
                        Matrix.CreateRotation(-hoveredRoomItem.Selectable.Rotation * (Math.PI / 180.0)) *
                        Matrix.CreateTranslation(-hoveredRoomItem.Selectable.Pivot.X, -hoveredRoomItem.Selectable.Pivot.Y)))
                    {
                        context.DrawRectangle(new Pen(new SolidColorBrush(selectedColor)), rect, 0);
                    }
                }
            }

            stopWatch.Stop();
            customDrawOperationTime = Math.Ceiling(stopWatch.Elapsed.TotalMilliseconds);

#if DEBUG
            RenderDebugText(context);
#endif
        }

        TopLevel topLevel = TopLevel.GetTopLevel(this)!;
        topLevel.RequestAnimationFrame(_ =>
        {
            InvalidateVisual();
        });
    }

    Color GetSelectedColor()
    {
        Color color = this.GetSolidColorBrushResource("SystemControlHighlightAccentBrush").Color;
        return new Color(128, color.R, color.G, color.B);
    }

    InteractionMode GetInteractionMode()
    {
        if (GetSelectedTilesLayer() is not null)
        {
            return InteractionMode.Tiles;
        }
        else if (IsRoomTilesSelected())
        {
            return InteractionMode.RoomTiles;
        }
        else
        {
            return InteractionMode.Items;
        }
    }

    UndertaleRoom.Layer? GetSelectedTilesLayer()
    {
        if (vm!.RoomTreeItemsSelectedItem is UndertaleRoom.Layer { LayerType: UndertaleRoom.LayerType.Tiles } tilesLayer)
        {
            return tilesLayer;
        }
        return null;
    }

    UndertaleRoom.Layer? GetSelectedLegacyTilesLayer()
    {
        if (vm!.RoomTreeItemsSelectedItem is UndertalePointerList<UndertaleRoom.Tile> && vm!.CategorySelected is UndertaleRoom.Layer { LayerType: UndertaleRoom.LayerType.Assets } layer)
            return layer;
        return null;
    }

    bool IsRoomTilesSelected()
    {
        if (vm!.PropertiesContent is UndertaleRoomViewModel.TilesViewModel)
            return true;
        return false;
    }

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

    void ItemHoverOnMoved(List<RoomItem> roomItems)
    {
        foreach (RoomItem roomItem in roomItems.Reverse<RoomItem>())
        {
            if (roomItem.Selectable is null)
                continue;

            if (vm!.IsSelectAnyLayerEnabled || vm!.CategorySelected is null || roomItem.Selectable.Category == vm!.CategorySelected)
                if (RectContainsPoint(roomItem.Selectable.Bounds, roomItem.Selectable.Rotation, roomItem.Selectable.Pivot, pointerPositionInRoom))
                {
                    hoveredItem = roomItem.Object;
                    break;
                }
        }
    }

    void ItemMoveOnPressed(List<RoomItem> roomItems)
    {
        RoomItem? hoveredRoomItem = GetRoomItemOfItem(roomItems, hoveredItem);

        if (hoveredRoomItem is not null && hoveredRoomItem.Selectable is not null)
        {
            RoomItemProperties properties = hoveredRoomItem.Selectable.GetProperties();
            itemMoveOffset = new(pointerPositionInRoom.X - properties.X, pointerPositionInRoom.Y - properties.Y);

            vm!.RoomTreeItemsSelectedItem = hoveredRoomItem.Object;
        }
        else
        {
            if (!vm!.IsSelectAnyLayerEnabled)
                vm!.RoomTreeItemsSelectedItem = vm.FindItemFromCategory(vm!.CategorySelected);
            else
                vm!.RoomTreeItemsSelectedItem = null;
        }
    }

    void ItemMoveOnMoved(List<RoomItem> roomItems, bool overrideGrid)
    {
        if (vm!.IsLocked)
            return;

        RoomItem? roomItem = GetSelectedRoomItem(roomItems);
        if (roomItem is not null && roomItem.Selectable is not null)
        {
            double x = pointerPositionInRoom.X - itemMoveOffset.X;
            double y = pointerPositionInRoom.Y - itemMoveOffset.Y;

            if (vm!.IsGridEnabled != overrideGrid)
            {
                x = (Math.Floor(pointerPositionInRoom.X / vm.GridWidth) * vm.GridWidth)
                    - (Math.Floor(itemMoveOffset.X / vm.GridWidth) * vm.GridWidth);
                y = (Math.Floor(pointerPositionInRoom.Y / vm.GridHeight) * vm.GridHeight)
                    - (Math.Floor(itemMoveOffset.Y / vm.GridHeight) * vm.GridHeight);
            }

            roomItem.Selectable.SetProperties(new((int)x, (int)y));
        }
    }

    RoomItem? GetRoomItemOfItem(List<RoomItem> roomItems, object? item)
    {
        if (item is null)
            return null;
        return roomItems.Find(x => x.Object == item);
    }

    RoomItem? GetSelectedRoomItem(List<RoomItem> roomItems)
    {
        RoomItem? res = GetRoomItemOfItem(roomItems, vm?.RoomTreeItemsSelectedItem);

        if (res is not null && res.Selectable is not null)
            return res;
        return null;
    }

    void FocusOnSelectedItem(List<RoomItem> roomItems)
    {
        RoomItem? item = GetSelectedRoomItem(roomItems);
        if (item is not null && item.Selectable is not null)
        {
            translation = new(-item.Selectable.Bounds.X * scaling + (Bounds.Width / 2), -item.Selectable.Bounds.Y * scaling + (Bounds.Height / 2));
        }
    }

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

    void SetLayerTileAtPointer(UndertaleRoom.Layer tilesLayer, uint tileData)
    {
        if (vm!.IsLocked)
            return;

        if (GetLayerTileIndexesAtPointer(tilesLayer, out (int x, int y) point))
        {
            if ((tileData & UndertaleRoomViewModel.TILE_ID) < tilesLayer.TilesData.Background.GMS2TileCount)
                tilesLayer.TilesData.TileData[point.y][point.x] = tileData;
        }
    }

    UndertaleRoom.Tile? GetRoomTileAtExactPosition(List<RoomItem> roomItems, double x, double y)
    {
        foreach (RoomItem roomItem in roomItems.Reverse<RoomItem>())
        {
            if (roomItem.Selectable is null)
                continue;

            UndertaleRoom.Layer? legacyTilesLayer = GetSelectedLegacyTilesLayer();

            if (roomItem.Selectable.Category.Equals((object?)legacyTilesLayer ?? "Tiles") && roomItem.Object is UndertaleRoom.Tile tile
                && tile.X == x && tile.Y == y)
            {
                return tile;
            }
        }
        return null;
    }

    UndertaleRoom.Tile? GetRoomTileAtPointer(List<RoomItem> roomItems)
    {
        foreach (RoomItem roomItem in roomItems.Reverse<RoomItem>())
        {
            if (roomItem.Selectable is null)
                continue;

            UndertaleRoom.Layer? legacyTilesLayer = GetSelectedLegacyTilesLayer();

            if (roomItem.Selectable.Category.Equals((object?)legacyTilesLayer ?? "Tiles") && roomItem.Object is UndertaleRoom.Tile tile
                && RectContainsPoint(roomItem.Selectable.Bounds, roomItem.Selectable.Rotation, roomItem.Selectable.Pivot, pointerPositionInRoom))
            {
                return tile;
            }
        }
        return null;
    }

    void SetRoomTileAtPointer(List<RoomItem> roomItems, UndertaleNamedResource? resource, Rect? sourceRect, bool overrideGrid)
    {
        if (vm!.IsLocked)
            return;

        if (resource is not null && sourceRect is Rect sourceRectNN)
        {
            double x = pointerPositionInRoom.X;
            double y = pointerPositionInRoom.Y;

            if (vm!.IsGridEnabled != overrideGrid)
            {
                x = (Math.Floor(pointerPositionInRoom.X / vm.GridWidth) * vm.GridWidth);
                y = (Math.Floor(pointerPositionInRoom.Y / vm.GridHeight) * vm.GridHeight);
            }

            UndertaleRoom.Tile? tile = GetRoomTileAtExactPosition(roomItems, x, y);
            if (tile is null)
            {
                UndertaleRoom.Layer? legacyTilesLayer = GetSelectedLegacyTilesLayer();
                if (legacyTilesLayer is not null)
                {
                    tile = vm!.AddLegacyTileInstance(legacyTilesLayer);
                }
                else
                {
                    tile = vm!.AddTile();
                }
            }

            tile.ObjectDefinition = resource;
            tile.SourceX = (int)sourceRectNN.X;
            tile.SourceY = (int)sourceRectNN.Y;
            tile.Width = (uint)sourceRectNN.Width;
            tile.Height = (uint)sourceRectNN.Height;

            tile.X = (int)x;
            tile.Y = (int)y;
        }
    }

    void RemoveRoomTileAtPointer(List<RoomItem> roomItems)
    {
        if (vm!.IsLocked)
            return;

        if (hoveredItem is UndertaleRoom.Tile tile)
        {
            UndertaleRoom.Layer? legacyTilesLayer = GetSelectedLegacyTilesLayer();
            if (legacyTilesLayer is not null)
            {
                vm.RemoveAsset(legacyTilesLayer.AssetsData.LegacyTiles, tile);
            }
            else
            {
                vm.RemoveTile(tile);
            }
            hoveredItem = null;
        }
    }

    void RenderDebugText(DrawingContext context)
    {
        // Debug text
        Point roomMousePosition = ((pointerPosition - translation) / scaling);

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
            $"custom render time: <{customDrawOperationTime} ms\n" +
            $"hovered item: {hoveredItem}\n" +
            $"hovered tile: {GetTileInfo(hoveredTile)}\n" +
            $"selected tile: {GetTileInfo(vm?.SelectedTileData)}",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, new SolidColorBrush(Colors.White)),
            new Point(0, 0));
    }

    static bool RectContainsPoint(Rect rect, double rotation, Point pivot, Point point)
    {
        return rect.Contains(point.Transform(Matrix.CreateRotation(double.DegreesToRadians(rotation), pivot)));
    }

    public class Updater()
    {
        public UndertaleRoom? Room = null;
        public readonly List<RoomItem> RoomItems = [];

        public static List<RoomItem> MakeRoomItems(UndertaleRoom room)
        {
            var updater = new Updater()
            {
                Room = room,
            };
            updater.Update();
            return updater.RoomItems;
        }

        public void Update()
        {
            RoomItems.Clear();

            if (Room is null)
                return;

            if (Room.Flags.HasFlag(UndertaleRoom.RoomEntryFlags.IsGMS2) || Room.Flags.HasFlag(UndertaleRoom.RoomEntryFlags.IsGM2024_13))
            {
                IOrderedEnumerable<UndertaleRoom.Layer> layers = Room.Layers.Reverse().OrderByDescending(x => x.LayerDepth);
                foreach (UndertaleRoom.Layer layer in layers)
                {
                    if (!layer.IsVisible)
                        continue;

                    switch (layer.LayerType)
                    {
                        case UndertaleRoom.LayerType.Path:
                        case UndertaleRoom.LayerType.Path2:
                            break;
                        case UndertaleRoom.LayerType.Background:
                            UpdateLayerBackground(layer);
                            break;
                        case UndertaleRoom.LayerType.Instances:
                            UpdateGameObjects(layer.InstancesData.Instances, layer);
                            break;
                        case UndertaleRoom.LayerType.Assets:
                            UpdateTiles(layer.AssetsData.LegacyTiles, layer);
                            UpdateSprites(layer.AssetsData.Sprites, layer);
                            // layer.AssetsData.Sequences
                            // layer.AssetsData.NineSlices
                            // layer.AssetsData.ParticleSystems
                            // layer.AssetsData.TextItems
                            break;
                        case UndertaleRoom.LayerType.Tiles:
                            UpdateLayerTiles(layer);
                            break;
                        case UndertaleRoom.LayerType.Effect:
                            // layer.EffectData
                            break;
                    }
                }
            }
            else
            {
                UpdateBackgrounds(Room.Backgrounds, foregrounds: false);
                UpdateTiles(Room.Tiles);
                UpdateGameObjects(Room.GameObjects);
                UpdateBackgrounds(Room.Backgrounds, foregrounds: true);
            }
        }

        void UpdateBackgrounds(IList<UndertaleRoom.Background> backgrounds, bool foregrounds)
        {
            foreach (var background in backgrounds)
            {
                if (background.Foreground == foregrounds)
                {
                    RoomItems.Add(new(
                       Object: background
                    ));
                }
            }
        }

        void UpdateLayerBackground(UndertaleRoom.Layer layer)
        {
            RoomItems.Add(new RoomItem(
                Object: layer
            ));
        }

        void UpdateTiles(IList<UndertaleRoom.Tile> roomTiles, UndertaleRoom.Layer? layer = null)
        {
            IOrderedEnumerable<UndertaleRoom.Tile> orderedRoomTiles = roomTiles.OrderByDescending(x => x.TileDepth);
            foreach (UndertaleRoom.Tile roomTile in orderedRoomTiles)
            {
                float x = (layer?.XOffset ?? 0) + roomTile.X;
                float y = (layer?.YOffset ?? 0) + roomTile.Y;
                float w = roomTile.Width * roomTile.ScaleX;
                float h = roomTile.Height * roomTile.ScaleY;

                RoomItems.Add(new RoomItem(
                    Object: roomTile,
                    Layer: layer,
                    Selectable: new(
                        Category: layer is not null ? layer : "Tiles",
                        Bounds: new Rect(x, y, w, h).Normalize(),
                        Rotation: 0,
                        Pivot: new Point(x, y),
                        GetProperties: () =>
                        {
                            return new(roomTile.X, roomTile.Y);
                        },
                        SetProperties: (properties) =>
                        {
                            roomTile.X = properties.X;
                            roomTile.Y = properties.Y;
                        }
                    )
                ));
            }
        }

        void UpdateLayerTiles(UndertaleRoom.Layer layer)
        {
            RoomItems.Add(new RoomItem(
                Object: layer
            ));
        }

        void UpdateSprites(IList<UndertaleRoom.SpriteInstance> roomSprites, UndertaleRoom.Layer layer)
        {
            foreach (UndertaleRoom.SpriteInstance roomSprite in roomSprites)
            {
                if (roomSprite.Sprite is null)
                    continue;
                if (!(roomSprite.FrameIndex >= 0 && roomSprite.FrameIndex < roomSprite.Sprite.Textures.Count))
                    continue;

                UndertaleTexturePageItem texture = roomSprite.Sprite.Textures[(int)roomSprite.FrameIndex].Texture;

                RoomItems.Add(new(
                    Object: roomSprite,
                    Layer: layer,
                    Selectable: new(
                        Category: layer,
                        Bounds: new Rect(
                            layer.XOffset + roomSprite.X - roomSprite.Sprite.OriginX * roomSprite.ScaleX,
                            layer.YOffset + roomSprite.Y - roomSprite.Sprite.OriginY * roomSprite.ScaleY,
                            texture.BoundingWidth * roomSprite.ScaleX,
                            texture.BoundingHeight * roomSprite.ScaleY
                        ).Normalize(),
                        Rotation: roomSprite.Rotation,
                        Pivot: new Point(layer.XOffset + roomSprite.X, layer.YOffset + roomSprite.Y),
                        GetProperties: () =>
                        {
                            return new(roomSprite.X, roomSprite.Y);
                        },
                        SetProperties: (properties) =>
                        {
                            roomSprite.X = properties.X;
                            roomSprite.Y = properties.Y;
                        }
                    )
                ));
            }
        }

        void UpdateGameObjects(IList<UndertaleRoom.GameObject> roomGameObjects, UndertaleRoom.Layer? layer = null)
        {
            foreach (UndertaleRoom.GameObject roomGameObject in roomGameObjects)
            {
                UndertaleGameObject? gameObject = roomGameObject.ObjectDefinition;
                if (gameObject is null ||
                    gameObject.Sprite is null ||
                    !(roomGameObject.ImageIndex >= 0 && roomGameObject.ImageIndex < gameObject.Sprite.Textures.Count))
                    continue;

                UndertaleTexturePageItem texture = gameObject.Sprite.Textures[roomGameObject.ImageIndex].Texture;

                RoomItems.Add(new(
                    Object: roomGameObject,
                    Selectable: new(
                        Category: layer is not null ? layer : "GameObjects",
                        Bounds: new Rect(
                            roomGameObject.X - gameObject.Sprite.OriginX * roomGameObject.ScaleX,
                            roomGameObject.Y - gameObject.Sprite.OriginY * roomGameObject.ScaleY,
                            texture.BoundingWidth * roomGameObject.ScaleX,
                            texture.BoundingHeight * roomGameObject.ScaleY
                        ).Normalize(),
                        Rotation: roomGameObject.Rotation,
                        Pivot: new Point(
                            roomGameObject.X,
                            roomGameObject.Y),
                        GetProperties: () =>
                        {
                            return new(roomGameObject.X, roomGameObject.Y);
                        },
                        SetProperties: (properties) =>
                        {
                            roomGameObject.X = properties.X;
                            roomGameObject.Y = properties.Y;
                        }
                    )
                ));
            }
        }
    }

    public class UndertaleReferenceDropHandler : DropHandlerBase
    {
        public override bool Validate(object? sender, DragEventArgs e, object? sourceContext, object? targetContext, object? state)
        {
            if (sender is UndertaleRoomEditor editor
                && sourceContext is DataExplorerViewModel.Item item
                && item.Value is UndertaleResource resource
                && targetContext is UndertaleRoomViewModel vm)
            {
                if (resource is UndertaleGameObject gameObject)
                {
                    return (vm.CategorySelected is "GameObjects"
                        || vm.CategorySelected is UndertaleRoom.Layer layer && layer.LayerType == UndertaleRoom.LayerType.Instances);
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
                && sourceContext is DataExplorerViewModel.Item item
                && item.Value is UndertaleResource resource
                && targetContext is UndertaleRoomViewModel vm)
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

                    return true;
                }
                else if (resource is UndertaleSprite sprite)
                {
                    if (vm.CategorySelected is UndertaleRoom.Layer layer && layer.LayerType == UndertaleRoom.LayerType.Assets)
                    {
                        vm.AddSpriteInstance(layer, sprite, x, y);
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            return false;
        }
    }
}
