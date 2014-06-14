﻿/* Copyright 2013 Shawn Henry, Rob Smith, and Michael Friedman
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System;
using System.Collections.Generic;
using System.Device.Location;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Controls.Maps;
using Microsoft.Phone.Controls.Maps.Core;
using Microsoft.Phone.Shell;
using OneBusAway.WP7.ViewModel;
using OneBusAway.WP7.ViewModel.AppDataDataStructures;
using OneBusAway.WP7.ViewModel.BusServiceDataStructures;
using System.Diagnostics;
using System.Windows.Threading;
using System.IO.IsolatedStorage;
using OneBusAway.WP7.ViewModel.LocationServiceDataStructures;

namespace OneBusAway.WP7.View
{
    public partial class MainPage : AViewPage
    {
        private MainPageVM viewModel;
        private bool firstLoad;
        private Popup popup;
        private bool navigatedAway;
        private Object navigationLock;
        private const string searchErrorMessage =
            "Search for a route: 44\r\n" +
            "Search by stop number: 11132\r\n" +
            "Find a landmark: Space Needle\r\n" +
            "Or an address: 1 Microsoft Way";

        public MainPage()
            : base()
        {
            InitializeComponent();
            base.Initialize();

            // It is the first launch of the app if this key doesn't exist.  Otherwise we are returning
            // to the main page after tombstoning and showing the splash screen looks bad
            if (PhoneApplicationService.Current.State.ContainsKey("ShowLoadingSplash") == false)
            {
                ShowLoadingSplash();
            }

            viewModel = aViewModel as MainPageVM;
            firstLoad = true;
            navigatedAway = false;
            navigationLock = new Object();

            this.Loaded += new RoutedEventHandler(MainPage_Loaded);

            SupportedOrientations = SupportedPageOrientation.Portrait;

            this.ApplicationBar.ForegroundColor = ((SolidColorBrush)Application.Current.Resources["OBAForegroundBrush"]).Color;
            // the native theme uses a shade of "gray" that is actually white or black with an alpha mask.
            // the appbar needs to be opaque.
            ColorAlphaConverter alphaConverter = new ColorAlphaConverter();
            SolidColorBrush appBarBrush = (SolidColorBrush)alphaConverter.Convert(
                                                            Application.Current.Resources["OBADarkBrush"], 
                                                            typeof(SolidColorBrush), 
                                                            Application.Current.Resources["OBABackgroundBrush"], 
                                                            null
                                                            );

            this.ApplicationBar.BackgroundColor = appBarBrush.Color;
        }

        private void ShowLoadingSplash()
        {
            ApplicationBar.IsVisible = false;

            this.popup = new Popup();
            this.popup.Child = new PopupSplash();
            this.popup.IsOpen = true;

            DispatcherTimer splashTimer = new DispatcherTimer();
            splashTimer.Interval = TimeSpan.FromSeconds(3);
            splashTimer.Tick += (sender, e) =>
                {
                    this.Dispatcher.BeginInvoke(HideLoadingSplash);
                    splashTimer.Stop();
                };
            splashTimer.Start();
        }

        private void HideLoadingSplash()
        {
            if (this.popup != null)
            {
                this.popup.IsOpen = false;
            }

            ApplicationBar.IsVisible = true;
            SystemTray.IsVisible = true;

#if SCREENSHOT
            SystemTray.IsVisible = false;
#endif
        }

        void MainPage_Loaded(object sender, RoutedEventArgs e)
        {

            if (firstLoad == true)
            {
                // Since this is the first load, pull down the bus and stop info
                viewModel.LoadInfoForLocation();

                object selectedPivot;

                // In this case, we've been re-created after a tombstone, resume their previous pivot
                if(PhoneApplicationService.Current.State.TryGetValue("MainPageSelectedPivot", out selectedPivot))
                {
                    PC.SelectedIndex = (int)(MainPagePivots)selectedPivot;
                }
                // The app was started fresh, not from tombstone.  Check pivot settings.  If there isn't a setting,
                // default to the last used pivot
                else if (IsolatedStorageSettings.ApplicationSettings.TryGetValue("DefaultMainPagePivot", out selectedPivot) 
                    && ((MainPagePivots)selectedPivot >= 0))
                {
                    PC.SelectedIndex = (int)(MainPagePivots)selectedPivot;
                }
                // Is is set to use the previous pivot, if this key doesn't exist just leave
                // the pivot selection at the default
                else if (IsolatedStorageSettings.ApplicationSettings.TryGetValue("LastUsedMainPagePivot", out selectedPivot))
                {
                    PC.SelectedIndex = (int)(MainPagePivots)selectedPivot;
                }
            }
            firstLoad = false;

            // Load favorites every time because they might have changed since the last load
            viewModel.LoadFavorites();

            viewModel.CheckForLocalTransitData(delegate(bool hasData)
            {
                Dispatcher.BeginInvoke(() =>
                    {
                        if (hasData == false)
                        {
                            MessageBox.Show(
                                "Currently the OneBusAway service does not support your location." +
                                "Many functions of this app will not work."
                                );
                        }
                    });
            });

            viewModel.LocationTracker.RunWhenLocationKnown(delegate(GeoCoordinate location)
                {
                    Dispatcher.BeginInvoke(() => StopsMap.Center = location);
                }
            );

            HideLoadingSplash();
        }

        #region Navigation

        protected override void OnNavigatedTo(System.Windows.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            viewModel.RegisterEventHandlers(Dispatcher);

            navigatedAway = false;
        }

        protected override void OnNavigatedFrom(System.Windows.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            // Store it in the state variable for tombstoning
            PhoneApplicationService.Current.State["ShowLoadingSplash"] = false;
            PhoneApplicationService.Current.State["MainPageSelectedPivot"] = (MainPagePivots)PC.SelectedIndex;

            // This is for the last-used pivot on fresh load
            IsolatedStorageSettings.ApplicationSettings["LastUsedMainPagePivot"] = (MainPagePivots)PC.SelectedIndex;

            viewModel.UnregisterEventHandlers();
        }

        /// <summary>
        /// Helper for the NavigationService.  Debounces navigation calls.
        /// </summary>
        /// <param name="target"></param>
        private void Navigate(Uri target)
        {
            lock (navigationLock)
            {
                if (navigatedAway == false)
                {
                    navigatedAway = true;
                    NavigationService.Navigate(target);
                }
            }
        }

        #endregion

        #region Search callbacks

        private void SearchByRouteCallback(List<Route> routes)
        {
            Dispatcher.BeginInvoke(() =>
            {
                SearchStoryboard.Seek(TimeSpan.Zero);
                SearchStoryboard.Stop();
                this.Focus();
            });

            if (routes.Count == 0)
            {
                Dispatcher.BeginInvoke(() => MessageBox.Show(searchErrorMessage, "No results found", MessageBoxButton.OK));
            }
            else
            {
                Dispatcher.BeginInvoke(() =>
                {
                    viewModel.CurrentViewState.CurrentRoutes = routes;
                    Navigate(new Uri("/BusDirectionPage.xaml", UriKind.Relative));
                });
            }
        }

        private void SearchByStopCallback(List<Stop> stops)
        {
            Dispatcher.BeginInvoke(() =>
            {
                SearchStoryboard.Seek(TimeSpan.Zero);
                SearchStoryboard.Stop();
                this.Focus();
            });

            if (stops.Count == 0)
            {
                Dispatcher.BeginInvoke(() => MessageBox.Show(searchErrorMessage, "No results found", MessageBoxButton.OK));
            }
            else
            {
                Dispatcher.BeginInvoke(() =>
                {
                    viewModel.CurrentViewState.CurrentRoute = null;
                    viewModel.CurrentViewState.CurrentRouteDirection = null;
                    viewModel.CurrentViewState.CurrentStop = stops[0];
                    viewModel.CurrentViewState.CurrentSearchLocation = null;

                    Navigate(new Uri("/DetailsPage.xaml", UriKind.Relative));
                });
            }
        }

        private void SearchByLocationCallback(LocationForQuery location)
        {
            Dispatcher.BeginInvoke(() =>
            {
                SearchStoryboard.Seek(TimeSpan.Zero);
                SearchStoryboard.Stop();
                this.Focus();
            });

            if (location == null)
            {
                Dispatcher.BeginInvoke(() => MessageBox.Show(searchErrorMessage, "No results found", MessageBoxButton.OK));
            }
            else
            {
                Dispatcher.BeginInvoke(() =>
                {
                    viewModel.CurrentViewState.CurrentRoute = null;
                    viewModel.CurrentViewState.CurrentRouteDirection = null;
                    viewModel.CurrentViewState.CurrentStop = null;
                    viewModel.CurrentViewState.CurrentSearchLocation = location;

                    Navigate(new Uri("/StopsMapPage.xaml", UriKind.Relative));
                });
            }
        }

        #endregion

        #region UI element event handlers

        private void appbar_refresh_Click(object sender, EventArgs e)
        {
            if (viewModel.operationTracker.Loading == false)
            {
                viewModel.LoadInfoForLocation(true);
            }
        }

        private void FavoritesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                FavoriteRouteAndStop favorite = (FavoriteRouteAndStop)e.AddedItems[0];
                viewModel.CurrentViewState.CurrentRoute = favorite.route;
                viewModel.CurrentViewState.CurrentStop = favorite.stop;
                viewModel.CurrentViewState.CurrentRouteDirection = favorite.routeStops;

                Navigate(new Uri("/DetailsPage.xaml", UriKind.Relative));
            }
        }

        private void RecentsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            FavoritesListBox_SelectionChanged(sender, e);
        }

        private void StopsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                viewModel.CurrentViewState.CurrentRoute = null;
                viewModel.CurrentViewState.CurrentRouteDirection = null;
                viewModel.CurrentViewState.CurrentStop = (Stop)e.AddedItems[0];

                Navigate(new Uri("/DetailsPage.xaml", UriKind.Relative));
            }
        }

        private void appbar_search_Click(object sender, EventArgs e)
        {
            if (SearchPanel.Opacity == 0)
            {
                SearchStoryboard.Begin();
                SearchInputBox.Focus();
                SearchInputBox.SelectAll();
            }
            else
            {
                ProcessSearch(SearchInputBox.Text);
            }
        }


        private void SearchInputBox_LostFocus(object sender, RoutedEventArgs e)
        {
            SearchStoryboard.Seek(TimeSpan.Zero);
            SearchStoryboard.Stop();
            this.Focus();
        }

        private void SearchInputBox_KeyUp(object sender, KeyEventArgs e)
        {
            string searchString = SearchInputBox.Text;

            if (e.Key == Key.Enter)
            {
                ProcessSearch(searchString);
            }
        }

        private void ProcessSearch(string searchString)
        {
            int routeNumber = 0;

            bool canConvert = int.TryParse(searchString, out routeNumber); //check if it's a number
            if (canConvert == true) //it's a route or stop number
            {
                int number = int.Parse(searchString);
                if (number < 1000) //route number
                {
                    viewModel.SearchByRoute(searchString, SearchByRouteCallback);
                }
                else //stop number
                {
                    viewModel.SearchByStop(searchString, SearchByStopCallback);
                }
            }
            else if (string.IsNullOrEmpty(searchString) == false) // Try to find the location
            {
                viewModel.SearchByAddress(searchString, SearchByLocationCallback);
            }

            SearchStoryboard.Seek(TimeSpan.Zero);
            SearchStoryboard.Stop();
            this.Focus();
        }

        private void appbar_settings_Click(object sender, EventArgs e)
        {
            Navigate(new Uri("/SettingsPage.xaml", UriKind.Relative));
        }

        private void appbar_about_Click(object sender, EventArgs e)
        {
            Navigate(new Uri("/AboutPage.xaml", UriKind.Relative));
        }

        private void stopsMapBtn_Click(object sender, RoutedEventArgs e)
        {
            viewModel.CurrentViewState.CurrentRoute = null;
            viewModel.CurrentViewState.CurrentRouteDirection = null;
            viewModel.CurrentViewState.CurrentSearchLocation = null;
            viewModel.CurrentViewState.CurrentStop = null;

            Navigate(new Uri("/StopsMapPage.xaml", UriKind.Relative));
        }

        private void RouteDirection_Tap(object sender, Microsoft.Phone.Controls.GestureEventArgs e)
        {
            RouteStops routeStops = (sender as FrameworkElement).DataContext as RouteStops;
            viewModel.CurrentViewState.CurrentRoutes = new List<Route>() { (Route)routeStops.route };

            viewModel.CurrentViewState.CurrentRoute = routeStops.route;
            viewModel.CurrentViewState.CurrentRouteDirection = routeStops;

            viewModel.CurrentViewState.CurrentStop = viewModel.CurrentViewState.CurrentRouteDirection.stops[0];
            foreach (Stop stop in viewModel.CurrentViewState.CurrentRouteDirection.stops)
            {
                // TODO: Make this call location-unknown safe.  The CurrentLocation could be unknown
                // at this point during a tombstoning scenario
                GeoCoordinate location = viewModel.LocationTracker.CurrentLocation;

                if (viewModel.CurrentViewState.CurrentStop.CalculateDistanceInMiles(location) > stop.CalculateDistanceInMiles(location))
                {
                    viewModel.CurrentViewState.CurrentStop = stop;
                }
            }

            Navigate(new Uri("/DetailsPage.xaml", UriKind.Relative));
        }

        private void PC_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            //we bind the DataContext only when the pivot is naivgated to. This improves perf if Favs or Recent are the first pivots
            FrameworkElement selectedElement = ((sender as Pivot).SelectedItem as PivotItem).Content as FrameworkElement;
            selectedElement.DataContext = viewModel;
        }

        #endregion

    }
}
